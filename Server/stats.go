package main

// Live statistics: per-stream frame rates and the machine's CPU and memory.
// Sampled every couple of seconds and served at GET /api/stats, read by the
// load-test tool and the streams test page.

import (
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"sort"
	"sync"
	"time"
)

const statsInterval = 2 * time.Second

type viewerStats struct {
	Who     string  `json:"who"`
	FPS     float64 `json:"fps"`
	Dropped float64 `json:"droppedPerS"`
	Skipped float64 `json:"skippedPerS"`
}

type streamStats struct {
	ID         string         `json:"id"`
	Label      string         `json:"label"` // tank name, where known
	Sources    int            `json:"sources"`
	FPSIn      float64        `json:"fpsIn"`
	KBPerFrame float64        `json:"kbPerFrame"`
	MbitIn     float64        `json:"mbitIn"`
	Tank       *tankLinkStats `json:"tank,omitempty"` // the tank's own report: Wi-Fi signal, send times
	Viewers    []viewerStats  `json:"viewers"`
}

type statsSnapshot struct {
	IntervalS float64       `json:"intervalS"`
	Streams   []streamStats `json:"streams"`
	System    *systemStats  `json:"system,omitempty"` // nil where /proc is unavailable (Windows)
}

type statsSampler struct {
	rl     *relay
	vlog   *videoLog
	mu     sync.Mutex
	latest statsSnapshot
}

func startStats(rl *relay, vlog *videoLog) *statsSampler {
	ss := &statsSampler{rl: rl, vlog: vlog}
	go ss.loop()
	return ss
}

func (ss *statsSampler) loop() {
	sys := newSystemSampler()
	last := time.Now()
	lastLog := time.Now()
	for range time.Tick(statsInterval) {
		secs := time.Since(last).Seconds()
		last = time.Now()

		snap := statsSnapshot{IntervalS: secs, System: sys.sample()}
		for _, s := range ss.rl.all() {
			frames := s.framesIn.Swap(0)
			bytes := s.bytesIn.Swap(0)
			st := streamStats{
				ID:      s.id,
				Label:   s.getLabel(),
				Sources: int(s.sources.Load()),
				FPSIn:   float64(frames) / secs,
				MbitIn:  float64(bytes) * 8 / secs / 1e6,
				Tank:    s.tankLink(),
				Viewers: []viewerStats{},
			}
			if frames > 0 {
				st.KBPerFrame = float64(bytes) / 1024 / float64(frames)
			}
			for _, v := range s.viewerList() {
				st.Viewers = append(st.Viewers, viewerStats{
					Who:     v.who,
					FPS:     float64(v.sent.Swap(0)) / secs,
					Dropped: float64(v.dropped.Swap(0)) / secs,
					Skipped: float64(v.skipped.Swap(0)) / secs,
				})
			}
			// Streams nobody is sending or watching are left out.
			if st.Sources == 0 && len(st.Viewers) == 0 && frames == 0 {
				continue
			}
			sort.Slice(st.Viewers, func(i, j int) bool { return st.Viewers[i].Who < st.Viewers[j].Who })
			snap.Streams = append(snap.Streams, st)
		}
		sort.Slice(snap.Streams, func(i, j int) bool { return naturalLess(snap.Streams[i].ID, snap.Streams[j].ID) })

		ss.mu.Lock()
		ss.latest = snap
		ss.mu.Unlock()

		var rows [][]string
		for _, st := range snap.Streams {
			// Only tanks sending video or being watched: idle tanks in the
			// lobby would fill the log with nothing.
			if st.FPSIn > 0 || len(st.Viewers) > 0 {
				rows = append(rows, streamRows(last, st)...)
			}
		}
		ss.vlog.rows(rows...)

		// One summary line a minute in the router's log while video is flowing.
		if len(snap.Streams) > 0 && time.Since(lastLog) >= time.Minute {
			lastLog = time.Now()
			logSummary(snap)
		}
	}
}

func (ss *statsSampler) handle(w http.ResponseWriter, r *http.Request) {
	ss.mu.Lock()
	snap := ss.latest
	ss.mu.Unlock()
	if snap.Streams == nil {
		snap.Streams = []streamStats{}
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(snap)
}

func logSummary(snap statsSnapshot) {
	var fps, mbit float64
	viewers := 0
	weakest := ""
	weakestRSSI := 0
	for _, s := range snap.Streams {
		fps += s.FPSIn
		mbit += s.MbitIn
		viewers += len(s.Viewers)
		if s.Tank != nil && s.Tank.RSSI != 0 && (weakest == "" || s.Tank.RSSI < weakestRSSI) {
			weakest, weakestRSSI = s.Label, s.Tank.RSSI
		}
	}
	if weakest != "" {
		weakest = fmt.Sprintf(" | weakest signal %s %d dBm", weakest, weakestRSSI)
	}
	cpu := ""
	if sys := snap.System; sys != nil {
		cpu = fmt.Sprintf(" | cpu %.0f%% (softirq %.0f%%, server %.0f%%)", sys.CPUBusyPct, sys.CPUSoftirqPct, sys.ProcCPUPct)
	}
	log.Printf("[VideoStats] %d streams, %.0f fps in, %.1f Mbit/s in, %d viewers%s%s",
		len(snap.Streams), fps, mbit, viewers, weakest, cpu)
}

func round1(v float64) float64 { return float64(int(v*10+0.5)) / 10 }

// naturalLess orders "2" before "10".
func naturalLess(a, b string) bool {
	if len(a) != len(b) {
		return len(a) < len(b)
	}
	return a < b
}
