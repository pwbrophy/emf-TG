// loadtest pretends to be a fleet of tanks and the phones watching them, to
// find out whether the server (on the router) can relay that much video.
//
// Each fake tank pushes an MJPEG stream of generated frames, sized like the
// real cameras' output, to the server. For each tank, fake viewers pull the
// relayed stream back: a "phone" at full frame rate and a "display" capped
// like the spectator screen. Every frame carries its send time, so the tool
// measures the real delay through the server. Tanks start one at a time, so
// the report shows where things start to struggle.
//
//	go run ./cmd/loadtest -server http://192.168.8.1:8088
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"image"
	"image/color"
	"image/jpeg"
	"io"
	"math/rand"
	"net/http"
	"os"
	"os/signal"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"thundergeddon/server/internal/mjpeg"
)

var (
	server     = flag.String("server", "http://192.168.8.1:8088", "server to test")
	numTanks   = flag.Int("tanks", 10, "number of fake tanks")
	fps        = flag.Int("fps", 20, "frames per second each tank sends")
	kbPerFrame = flag.Float64("kb", 11, "target JPEG size in KB (real tanks: about 8-15)")
	width      = flag.Int("width", 320, "frame width")
	height     = flag.Int("height", 240, "frame height")
	phones     = flag.Int("phones", 1, "full-rate viewers per tank (the player's phone)")
	displayFPS = flag.Int("display-fps", 15, "frame-rate cap of the spectator-display viewer per tank; 0 = no display viewer")
	ramp       = flag.Duration("ramp", 10*time.Second, "time between starting each tank; 0 = all at once")
	hold       = flag.Duration("hold", 60*time.Second, "how long to keep running once every tank has started")
	every      = flag.Duration("report", 5*time.Second, "how often to print a line")
)

var client = &http.Client{Transport: &http.Transport{
	MaxIdleConnsPerHost:   64,
	ResponseHeaderTimeout: 10 * time.Second,
	DisableCompression:    true,
}}

type tank struct {
	id         int
	frames     [][]byte
	sent       atomic.Int64
	reconnects atomic.Int64
}

type viewer struct {
	tank       int
	kind       string // "phone" or "display"
	got        atomic.Int64
	reconnects atomic.Int64

	mu      sync.Mutex
	delays  []time.Duration
	started bool
}

func main() {
	flag.Parse()
	*server = strings.TrimRight(*server, "/")

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	if _, err := fetchStats(); err != nil {
		fmt.Fprintf(os.Stderr, "Can't reach %s: %v\n", *server, err)
		os.Exit(1)
	}

	fmt.Printf("Making frames (%dx%d, target %.0f KB)...\n", *width, *height, *kbPerFrame)
	quality := tuneQuality(*kbPerFrame)
	tanks := make([]*tank, *numTanks)
	var avgKB float64
	for i := range tanks {
		tanks[i] = &tank{id: i + 1, frames: makeFrames(i+1, quality)}
		for _, f := range tanks[i].frames {
			avgKB += float64(len(f)) / 1024
		}
	}
	avgKB /= float64(*numTanks * len(tanks[0].frames))
	perTankMbit := avgKB * 1024 * 8 * float64(*fps) / 1e6
	fmt.Printf("Frames: %.1f KB average (JPEG quality %d) = %.2f Mbit/s per tank at %d fps\n", avgKB, quality, perTankMbit, *fps)
	fmt.Printf("At full load: %.1f Mbit/s into the server, %.1f Mbit/s out\n\n",
		perTankMbit*float64(*numTanks),
		perTankMbit*float64(*numTanks)*(float64(*phones)+float64(min(*displayFPS, *fps))/float64(*fps)))

	var viewers []*viewer
	var vmu sync.Mutex
	active := atomic.Int32{}
	allStarted := make(chan time.Time, 1)

	// Start tanks one at a time, each with its viewers.
	go func() {
		for i, t := range tanks {
			if i > 0 && *ramp > 0 {
				select {
				case <-ctx.Done():
					return
				case <-time.After(*ramp):
				}
			}
			go t.run(ctx)
			var vs []*viewer
			for p := 0; p < *phones; p++ {
				vs = append(vs, &viewer{tank: t.id, kind: "phone"})
			}
			if *displayFPS > 0 {
				vs = append(vs, &viewer{tank: t.id, kind: "display"})
			}
			for _, v := range vs {
				go v.run(ctx)
			}
			vmu.Lock()
			viewers = append(viewers, vs...)
			vmu.Unlock()
			active.Add(1)
		}
		allStarted <- time.Now()
	}()

	start := time.Now()
	var holdRows []row
	var holdStart time.Time
	deadline := time.Duration(0)

	printHeader()
	ticker := time.NewTicker(*every)
	defer ticker.Stop()
	last := time.Now()
	lastSent := map[int]int64{}
	lastGot := map[*viewer]int64{}
	var lastReconn int64

loop:
	for {
		select {
		case <-ctx.Done():
			break loop
		case t := <-allStarted:
			holdStart = t
			deadline = time.Since(start) + *hold
			continue
		case <-ticker.C:
		}

		secs := time.Since(last).Seconds()
		last = time.Now()

		r := row{elapsed: time.Since(start), tanks: int(active.Load())}
		var tankFPS []float64
		var reconn int64
		for _, t := range tanks[:r.tanks] {
			n := t.sent.Load()
			tankFPS = append(tankFPS, float64(n-lastSent[t.id])/secs)
			lastSent[t.id] = n
			reconn += t.reconnects.Load()
		}
		vmu.Lock()
		vs := append([]*viewer(nil), viewers...)
		vmu.Unlock()
		var phoneFPS, displayFPSs []float64
		var delays []time.Duration
		for _, v := range vs {
			n := v.got.Load()
			f := float64(n-lastGot[v]) / secs
			lastGot[v] = n
			if v.kind == "phone" {
				phoneFPS = append(phoneFPS, f)
			} else {
				displayFPSs = append(displayFPSs, f)
			}
			delays = append(delays, v.takeDelays()...)
			reconn += v.reconnects.Load()
		}
		r.tankFPS, _ = avgMin(tankFPS)
		r.phoneFPS, r.phoneMin = avgMin(phoneFPS)
		r.displayFPS, _ = avgMin(displayFPSs)
		r.p50, r.p95, r.max = percentiles(delays)
		r.reconnects = reconn - lastReconn
		lastReconn = reconn
		if st, err := fetchStats(); err == nil {
			r.sys = st.System
			for _, s := range st.Streams {
				for _, v := range s.Viewers {
					r.serverDropped += v.Dropped
				}
			}
		} else {
			r.statsErr = true
		}
		r.print()

		if !holdStart.IsZero() && time.Since(holdStart) > *every {
			holdRows = append(holdRows, r)
		}
		if deadline > 0 && time.Since(start) >= deadline {
			break
		}
	}
	stop()
	summarize(holdRows)
}

// ── Fake tank ────────────────────────────────────────────────────────────────

func (t *tank) run(ctx context.Context) {
	for ctx.Err() == nil {
		err := t.stream(ctx)
		if ctx.Err() != nil {
			return
		}
		t.reconnects.Add(1)
		fmt.Printf("  tank %d: stream ended (%v), reconnecting\n", t.id, err)
		sleep(ctx, time.Second)
	}
}

func (t *tank) stream(ctx context.Context) error {
	pr, pw := io.Pipe()
	req, _ := http.NewRequestWithContext(ctx, http.MethodPost, fmt.Sprintf("%s/api/ingest/%d", *server, t.id), pr)
	req.Header.Set("Content-Type", mjpeg.ContentType)

	done := make(chan error, 1)
	go func() {
		resp, err := client.Do(req)
		if err == nil {
			body, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
			resp.Body.Close()
			err = fmt.Errorf("server closed the upload: %s %s", resp.Status, strings.TrimSpace(string(body)))
		}
		pw.CloseWithError(err)
		done <- err
	}()

	err := t.pump(ctx, pw)
	pw.Close()
	if upErr := <-done; err == nil || errors.Is(err, io.ErrClosedPipe) {
		err = upErr
	}
	return err
}

func (t *tank) pump(ctx context.Context, w io.Writer) error {
	if err := mjpeg.WriteStart(w); err != nil {
		return err
	}
	tick := time.NewTicker(time.Second / time.Duration(*fps))
	defer tick.Stop()
	for i := 0; ; i++ {
		select {
		case <-ctx.Done():
			return nil
		case <-tick.C:
		}
		f := mjpeg.Frame{Data: t.frames[i%len(t.frames)], Timestamp: strconv.FormatInt(time.Now().UnixNano(), 10)}
		if err := mjpeg.WriteFrame(w, f); err != nil {
			return err
		}
		t.sent.Add(1)
	}
}

// ── Fake viewer ──────────────────────────────────────────────────────────────

func (v *viewer) run(ctx context.Context) {
	url := fmt.Sprintf("%s/api/stream/%d?who=sim-%s", *server, v.tank, v.kind)
	if v.kind == "display" {
		url += "&maxfps=" + strconv.Itoa(*displayFPS)
	}
	for ctx.Err() == nil {
		err := v.watch(ctx, url)
		if ctx.Err() != nil {
			return
		}
		v.reconnects.Add(1)
		fmt.Printf("  %s viewer of tank %d: stream ended (%v), reconnecting\n", v.kind, v.tank, err)
		sleep(ctx, time.Second)
	}
}

func (v *viewer) watch(ctx context.Context, url string) error {
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("server said %s", resp.Status)
	}
	err = mjpeg.ReadFrames(resp.Body, 1<<20, func(f mjpeg.Frame) error {
		v.got.Add(1)
		if ns, err := strconv.ParseInt(f.Timestamp, 10, 64); err == nil {
			v.mu.Lock()
			v.delays = append(v.delays, time.Since(time.Unix(0, ns)))
			v.mu.Unlock()
		}
		return nil
	})
	if err == nil {
		err = errors.New("stream closed by server")
	}
	return err
}

func (v *viewer) takeDelays() []time.Duration {
	v.mu.Lock()
	defer v.mu.Unlock()
	d := v.delays
	v.delays = nil
	return d
}

// ── Frames ───────────────────────────────────────────────────────────────────

const framesPerTank = 40

// makeFrames draws a loop of frames: the tank's own colour with camera-like
// noise, its number in big digits, and a bar sweeping across so the picture
// visibly moves when viewed on a phone.
func makeFrames(tankID, quality int) [][]byte {
	rng := rand.New(rand.NewSource(int64(tankID)))
	base := tankColor(tankID)
	out := make([][]byte, framesPerTank)
	for i := range out {
		img := image.NewRGBA(image.Rect(0, 0, *width, *height))
		barX := i * *width / framesPerTank
		for y := 0; y < *height; y++ {
			for x := 0; x < *width; x++ {
				c := base
				// Gentle vertical gradient, like a lit scene.
				shade := 0.6 + 0.4*float64(y)/float64(*height)
				n := rng.Intn(49) - 24
				r := clamp(int(float64(c.R)*shade) + n)
				g := clamp(int(float64(c.G)*shade) + n)
				b := clamp(int(float64(c.B)*shade) + n)
				if x >= barX && x < barX+*width/16 {
					r, g, b = clamp(r+120), clamp(g+120), clamp(b+120)
				}
				img.SetRGBA(x, y, color.RGBA{uint8(r), uint8(g), uint8(b), 255})
			}
		}
		drawNumber(img, tankID, 16, 16, *height/8)
		drawNumber(img, i, 16, *height-16-*height/20*5, *height/20)
		var buf bytes.Buffer
		_ = jpeg.Encode(&buf, img, &jpeg.Options{Quality: quality})
		out[i] = buf.Bytes()
	}
	return out
}

// tuneQuality finds the JPEG quality whose frames come closest to the target size.
func tuneQuality(targetKB float64) int {
	lo, hi := 5, 95
	for lo < hi {
		mid := (lo + hi) / 2
		frames := makeFramesN(1, mid, 4)
		var kb float64
		for _, f := range frames {
			kb += float64(len(f)) / 1024
		}
		if kb/float64(len(frames)) < targetKB {
			lo = mid + 1
		} else {
			hi = mid
		}
	}
	return lo
}

func makeFramesN(tankID, quality, n int) [][]byte {
	all := makeFrames(tankID, quality)
	return all[:n]
}

func tankColor(id int) color.RGBA {
	palette := []color.RGBA{
		{200, 60, 40, 255}, {40, 140, 200, 255}, {60, 170, 70, 255}, {200, 160, 40, 255}, {150, 70, 190, 255},
		{40, 180, 170, 255}, {210, 90, 150, 255}, {120, 120, 120, 255}, {180, 110, 50, 255}, {90, 100, 210, 255},
	}
	return palette[(id-1)%len(palette)]
}

// 3x5 pixel digits, scaled up.
var digits = [10][5]string{
	{"###", "#.#", "#.#", "#.#", "###"}, {".#.", "##.", ".#.", ".#.", "###"},
	{"###", "..#", "###", "#..", "###"}, {"###", "..#", "###", "..#", "###"},
	{"#.#", "#.#", "###", "..#", "..#"}, {"###", "#..", "###", "..#", "###"},
	{"###", "#..", "###", "#.#", "###"}, {"###", "..#", "..#", "..#", "..#"},
	{"###", "#.#", "###", "#.#", "###"}, {"###", "#.#", "###", "..#", "###"},
}

func drawNumber(img *image.RGBA, n, x0, y0, scale int) {
	px := max(scale/5, 1)
	for _, ch := range strconv.Itoa(n) {
		d := digits[ch-'0']
		for row := 0; row < 5; row++ {
			for col := 0; col < 3; col++ {
				if d[row][col] != '#' {
					continue
				}
				for dy := 0; dy < px; dy++ {
					for dx := 0; dx < px; dx++ {
						img.SetRGBA(x0+col*px+dx, y0+row*px+dy, color.RGBA{255, 255, 255, 255})
					}
				}
			}
		}
		x0 += 4 * px
	}
}

func clamp(v int) int { return max(0, min(255, v)) }

// ── Reporting ────────────────────────────────────────────────────────────────

type sysStats struct {
	Cores         int       `json:"cores"`
	CPUBusyPct    float64   `json:"cpuBusyPct"`
	CPUCoresPct   []float64 `json:"cpuCoresPct"`
	CPUSoftirqPct float64   `json:"cpuSoftirqPct"`
	ProcCPUPct    float64   `json:"procCpuPct"`
	RSSMB         float64   `json:"rssMB"`
	MemAvailMB    float64   `json:"memAvailMB"`
}

type serverStats struct {
	Streams []struct {
		Viewers []struct {
			Dropped float64 `json:"droppedPerS"`
		} `json:"viewers"`
	} `json:"streams"`
	System *sysStats `json:"system"`
}

func fetchStats() (*serverStats, error) {
	resp, err := client.Get(*server + "/api/stats")
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	var st serverStats
	return &st, json.NewDecoder(resp.Body).Decode(&st)
}

type row struct {
	elapsed            time.Duration
	tanks              int
	tankFPS            float64
	phoneFPS, phoneMin float64
	displayFPS         float64
	p50, p95, max      time.Duration
	reconnects         int64
	serverDropped      float64
	sys                *sysStats
	statsErr           bool
}

func printHeader() {
	fmt.Println("         |       |  frames per second   |   delay (ms)   |        |            server machine")
	fmt.Println("  time   | tanks | sent  phone(min) disp| p50  p95  max  | reconn | cpu   per core   softirq server  RAM free")
	fmt.Println("  -------+-------+----------------------+----------------+--------+-------------------------------------------")
}

func (r row) print() {
	sys := "(no CPU figures: server isn't on Linux)"
	if r.statsErr {
		sys = "(couldn't read server stats)"
	} else if r.sys != nil {
		cores := make([]string, len(r.sys.CPUCoresPct))
		for i, c := range r.sys.CPUCoresPct {
			cores[i] = fmt.Sprintf("%.0f", c)
		}
		sys = fmt.Sprintf("%3.0f%%  %-9s  %5.0f%%  %5.0f%%  %4.0f MB",
			r.sys.CPUBusyPct, strings.Join(cores, "/"), r.sys.CPUSoftirqPct, r.sys.ProcCPUPct, r.sys.MemAvailMB)
	}
	fmt.Printf("  %5s  | %5d | %4.1f  %4.1f(%4.1f) %4.1f| %4d %4d %4d  | %6d | %s\n",
		fmtElapsed(r.elapsed), r.tanks, r.tankFPS, r.phoneFPS, r.phoneMin, r.displayFPS,
		r.p50.Milliseconds(), r.p95.Milliseconds(), r.max.Milliseconds(), r.reconnects, sys)
}

func summarize(rows []row) {
	fmt.Println()
	if len(rows) == 0 {
		fmt.Println("Stopped before all tanks were running, so there is no full-load result.")
		return
	}
	var phone, phoneMin, disp, cpu, cpuPeak, soft, proc, memMin float64
	var p95, worst time.Duration
	var reconn int64
	phoneMin, memMin = 1e9, 1e9
	for _, r := range rows {
		phone += r.phoneFPS
		phoneMin = minF(phoneMin, r.phoneMin)
		disp += r.displayFPS
		if r.p95 > p95 {
			p95 = r.p95
		}
		if r.max > worst {
			worst = r.max
		}
		reconn += r.reconnects
		if r.sys != nil {
			cpu += r.sys.CPUBusyPct
			cpuPeak = maxF(cpuPeak, r.sys.CPUBusyPct)
			soft += r.sys.CPUSoftirqPct
			proc += r.sys.ProcCPUPct
			memMin = minF(memMin, r.sys.MemAvailMB)
		}
	}
	n := float64(len(rows))
	target := float64(*fps)

	fmt.Printf("RESULT with all %d tanks running (%s):\n", *numTanks, fmtElapsed(time.Duration(len(rows))*(*every)))
	verdict("Phone video frame rate", phone/n >= target*0.95 && phoneMin >= target*0.85,
		fmt.Sprintf("%.1f fps average, worst single viewer %.1f (target %d)", phone/n, phoneMin, *fps))
	if *displayFPS > 0 {
		want := float64(min(*displayFPS, *fps))
		verdict("Display video frame rate", disp/n >= want*0.9, fmt.Sprintf("%.1f fps average (target %.0f)", disp/n, want))
	}
	verdict("Video delay through server", p95 < 150*time.Millisecond,
		fmt.Sprintf("worst p95 %d ms, worst single frame %d ms", p95.Milliseconds(), worst.Milliseconds()))
	verdict("Connections stayed up", reconn == 0, fmt.Sprintf("%d reconnects", reconn))
	if rows[0].sys != nil {
		verdict("Router CPU headroom", cpuPeak < 75,
			fmt.Sprintf("%.0f%% average, %.0f%% peak (network interrupts %.0f%%, server %.0f%%)", cpu/n, cpuPeak, soft/n, proc/n))
		verdict("Router memory", memMin > 100, fmt.Sprintf("%.0f MB free at the lowest", memMin))
	}
}

func verdict(what string, ok bool, detail string) {
	mark := "OK  "
	if !ok {
		mark = "FAIL"
	}
	fmt.Printf("  [%s] %-28s %s\n", mark, what, detail)
}

func avgMin(v []float64) (avg, lo float64) {
	if len(v) == 0 {
		return 0, 0
	}
	lo = v[0]
	for _, x := range v {
		avg += x
		lo = minF(lo, x)
	}
	return avg / float64(len(v)), lo
}

func percentiles(d []time.Duration) (p50, p95, mx time.Duration) {
	if len(d) == 0 {
		return
	}
	sort.Slice(d, func(i, j int) bool { return d[i] < d[j] })
	return d[len(d)/2], d[len(d)*95/100], d[len(d)-1]
}

func fmtElapsed(d time.Duration) string {
	s := int(d.Seconds())
	return fmt.Sprintf("%d:%02d", s/60, s%60)
}

func minF(a, b float64) float64 { return min(a, b) }
func maxF(a, b float64) float64 { return max(a, b) }

func sleep(ctx context.Context, d time.Duration) {
	select {
	case <-ctx.Done():
	case <-time.After(d):
	}
}
