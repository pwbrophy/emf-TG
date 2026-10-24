package main

// Signal Quality Test (host: Server Config → Signal Quality Test).
//
// Some tanks' Wi-Fi breaks down when their turret sits at certain angles to
// the hull (the boards couple; NS1020CA sheets under the turret board fix
// it when they're in place). This test turns every connected turret slowly
// through full circles, then back the other way, and records the radio at
// each angle, so a tank's weak angles show up and a fix can be checked
// against a baseline.
//
// Angle: the tanks stand in pairs, hull front to hull front about 1.5 m
// apart (1↔6, 2↔7, 3↔8, 4↔9, 5↔10), turrets forward. Each time a turret
// points at its partner, the partner's front IR sensors see its barrel: that
// moment is 0°. In between, the angle is interpolated in time, so each tank's
// own speed (and any slowing as it goes) is taken into account. Partners
// take turns, a few times a second: one keeps its barrel lit while the other
// listens, so no tank listens to its own beam. Only the front sensors count,
// so another pair's beam sweeping past from the side is ignored.
//
// Measured, by angle:
//   - the router's view of the tank: signal (dBm) and receive rate, read
//     from the router's Wi-Fi driver several times a second (iwinfo; only
//     when the hub runs on the router). This is the tank's sending side,
//     which is where the fault shows.
//   - the tank's video: frames reaching the hub per second, and the longest
//     wait between two frames.
//
// Cameras stream during the test whatever the phase; it can't run during a
// game, and a game can't start during it.

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"math"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// SignalTestSettings are the host's choices for the test (Settings.SignalTest).
type SignalTestSettings struct {
	Speed    float64 `json:"speed"`    // turret speed, 0..1: as slow as still turns steadily
	Turns    int     `json:"turns"`    // full turns each way
	Reverse  bool    `json:"reverse"`  // then the same number back the other way
	MaxTurnS float64 `json:"maxTurnS"` // give up on a tank if a turn takes longer than this
	// Drive: the tanks stand lifted and their tracks run a fixed game-like
	// pattern while the turrets turn, so the drive motors run as in a game
	// (a separate kind of run; signaltest_drive.go).
	Drive bool `json:"drive"`
	// DrivePattern: which pattern a run used (set when it starts).
	DrivePattern string `json:"drivePattern,omitempty"`
}

func defaultSignalTest() SignalTestSettings {
	return SignalTestSettings{Speed: 0.2, Turns: 3, Reverse: true, MaxTurnS: 90}
}

func (s *SignalTestSettings) clamp() {
	if s.Speed == 0 {
		*s = defaultSignalTest()
	}
	s.Speed = max(0.05, min(s.Speed, 1))
	s.Turns = max(1, min(s.Turns, 10))
	s.MaxTurnS = max(10, min(s.MaxTurnS, 600))
}

const (
	sigBins      = 36              // 10° each
	sigFrontMask = 0x83            // N, NE, NW sensors: the partner straight ahead
	sigListenMs  = 250             // each listening window
	sigWarmup    = 4 * time.Second // cameras start, motors on
	// Hits further apart than this start a new pass of the beam. Each tank's
	// partner listens about every 0.84 s and can miss a window at a beam's
	// edge (Jungle-06, 21 October 2026: hits 1.7 s apart in one beam, which
	// 1.5 s split into two "turns"). Beams a turn apart are much further.
	sigRunGap      = 3 * time.Second
	sigMinTurn     = 4.0             // seconds: no turret turns faster in this test
	sigStartIgnore = 2 * time.Second // a beam already in view as a pass begins is the start, not a turn
	sigRouterEvery = 250 * time.Millisecond
	sigDefaultHalf = 400 * time.Millisecond // half a beam's width in time, before one is measured
)

type signalTest struct {
	tanks  tankControl
	rl     *relay
	iface  string // the router's Wi-Fi interface for its view of the tanks; "" = none
	dir    string // where finished runs are kept (on the router: flash, next to the settings)
	onDone func() // called (without st.mu) when a run ends: cameras back to normal

	mu          sync.Mutex
	run         *sigRun                // the current or last run
	triedLatest bool                   // after a restart, the last saved run has been looked for
	infos       map[string]sigRunInfo  // saved runs' headlines, by file name
	views       map[string]sigSnapshot // saved runs, binned, by file name
	comments    map[string]string      // the host's comment on each run, by file name (comments.json)
}

// newSignalTest keeps finished runs in dir. Runs saved by older versions in
// oldDirs (the temp folder, which the router loses when it restarts) are
// copied there.
func newSignalTest(tanks tankControl, rl *relay, iface, dir string, oldDirs ...string) *signalTest {
	st := &signalTest{tanks: tanks, rl: rl, iface: iface, dir: dir,
		infos: map[string]sigRunInfo{}, views: map[string]sigSnapshot{}, comments: map[string]string{}}
	if dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			log.Printf("[SignalTest] can't keep runs in %s: %v", dir, err)
			st.dir = ""
		}
	}
	if st.dir != "" {
		for _, od := range oldDirs {
			st.adopt(od)
		}
		if b, err := os.ReadFile(filepath.Join(st.dir, sigCommentsFile)); err == nil {
			_ = json.Unmarshal(b, &st.comments)
		}
	}
	return st
}

type sigRun struct {
	started, ended time.Time // ended zero while running
	phase          string    // warm-up, turning, done, stopped
	note           string
	cfg            SignalTestSettings
	cancel         context.CancelFunc
	order          []string
	tanks          map[string]*sigTank
	routerOK       bool
	routerNote     string
	listenAt       map[string]time.Time // listener → middle of its current window
	driveDone      chan struct{}        // closed when the driving has stopped (Settings.Drive)
	name           string               // the file it's saved as (comments are kept by it)
	file           string               // saved run, once finished
}

type sigTank struct {
	id, name, partner string
	num               int
	passes            []*sigPass
	hits              []time.Time // the partner's front sensors saw this barrel
	router            []sigRouter
	frames            []time.Time // copied from the relay when the run ends
	finished          bool
}

type sigPass struct {
	dir        int // +1 first direction, -1 back
	start, end time.Time
	timedOut   bool
	finalFirst time.Time // when the last turn's beam was first seen
	stopAt     time.Time // half a beam later: stop then, facing forward
}

type sigRouter struct {
	at     time.Time
	dbm    int
	rxMbit float64
}

func (st *signalTest) active() bool {
	if st == nil {
		return false
	}
	st.mu.Lock()
	defer st.mu.Unlock()
	return st.run != nil && st.run.ended.IsZero()
}

var tankNumRe = regexp.MustCompile(`(\d+)\s*$`)

// tankNumber is the number a tank's name ends with (Desert-01 → 1), 0 = none.
func tankNumber(name string) int {
	m := tankNumRe.FindStringSubmatch(name)
	if m == nil {
		return 0
	}
	n, _ := strconv.Atoi(m[1])
	return n
}

// partnerNumber pairs 1↔6, 2↔7, 3↔8, 4↔9, 5↔10.
func partnerNumber(n int) int {
	switch {
	case n >= 1 && n <= 5:
		return n + 5
	case n >= 6 && n <= 10:
		return n - 5
	}
	return 0
}

// start begins a run with the connected tanks. Caller holds game.mu; nothing
// here calls back into the game.
func (st *signalTest) start(cfg SignalTestSettings, tanks []tankInfo) error {
	st.mu.Lock()
	defer st.mu.Unlock()
	if st.run != nil && st.run.ended.IsZero() {
		return fmt.Errorf("the signal test is already running")
	}
	if len(tanks) == 0 {
		return fmt.Errorf("no tanks are connected")
	}
	cfg.clamp()
	cfg.DrivePattern = ""
	if cfg.Drive {
		cfg.DrivePattern = sigDrivePatternName
	}
	ctx, cancel := context.WithCancel(context.Background())
	now := time.Now()
	r := &sigRun{started: now, phase: "warm-up", cfg: cfg, cancel: cancel, name: sigRunName(now),
		tanks: map[string]*sigTank{}, listenAt: map[string]time.Time{}}
	byNum := map[int]string{}
	for _, t := range tanks {
		n := tankNumber(t.Name)
		r.tanks[t.ID] = &sigTank{id: t.ID, name: t.Name, num: n}
		r.order = append(r.order, t.ID)
		if n > 0 {
			byNum[n] = t.ID
		}
	}
	sort.Slice(r.order, func(i, j int) bool {
		return naturalLess(r.tanks[r.order[i]].name, r.tanks[r.order[j]].name)
	})
	var missing []string
	for _, t := range r.tanks {
		if p := byNum[partnerNumber(t.num)]; p != "" {
			t.partner = p
		} else {
			missing = append(missing, t.name)
		}
	}
	if len(missing) > 0 {
		sort.Strings(missing)
		r.note = "No partner (no 0° mark, angles estimated): " + strings.Join(missing, ", ")
	}
	st.run = r
	for _, id := range r.order {
		st.rl.get(id).startRecording()
	}
	log.Printf("[SignalTest] starting with %d tanks: speed %.2f, %d turns, reverse %v, driving %v", len(r.order), cfg.Speed, cfg.Turns, cfg.Reverse, cfg.Drive)
	go st.runTest(ctx, r)
	return nil
}

// stop ends the current run early.
func (st *signalTest) stop() {
	st.mu.Lock()
	defer st.mu.Unlock()
	if st.run != nil && st.run.ended.IsZero() {
		st.run.phase = "stopping"
		st.run.cancel()
	}
}

func (st *signalTest) send(id, msg string) { st.tanks.sendTo(id, msg) }

func sleepCtx(ctx context.Context, d time.Duration) bool {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-t.C:
		return true
	}
}

func (st *signalTest) runTest(ctx context.Context, r *sigRun) {
	ids := r.order
	turret := func(id string, speed float64) {
		st.send(id, fmt.Sprintf(`{"cmd":"turret","speed":%.3f}`, speed))
	}
	defer st.finish(r)

	for _, id := range ids {
		st.send(id, `{"cmd":"motors_on"}`)
		turret(id, 0)
	}
	if !sleepCtx(ctx, sigWarmup) {
		return
	}
	go st.irLoop(ctx, r)
	go st.routerLoop(ctx, r)
	if r.cfg.Drive {
		r.driveDone = make(chan struct{})
		go st.driveLoop(ctx, r.order, r.driveDone)
	}

	st.mu.Lock()
	r.phase = "turning"
	now := time.Now()
	for _, id := range ids {
		t := r.tanks[id]
		t.passes = []*sigPass{{dir: 1, start: now}}
	}
	st.mu.Unlock()
	for _, id := range ids {
		turret(id, r.cfg.Speed)
	}

	tick := time.NewTicker(100 * time.Millisecond)
	defer tick.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
		}
		now := time.Now()
		st.mu.Lock()
		allDone := true
		var cmds []func()
		for _, id := range ids {
			t := r.tanks[id]
			if t.finished {
				continue
			}
			allDone = false
			p := t.passes[len(t.passes)-1]
			ended := false
			switch {
			case !p.stopAt.IsZero():
				ended = !now.Before(p.stopAt)
			case now.Sub(p.start) > time.Duration(float64(r.cfg.Turns)*r.cfg.MaxTurnS*float64(time.Second)):
				p.timedOut, ended = true, true
			default:
				// The last turn's beam has just been seen: carry on for half a
				// beam's width so the turret stops facing its partner.
				if first, ok := t.finalBeamStart(p, r.cfg.Turns); ok {
					p.finalFirst = first
					p.stopAt = first.Add(t.halfBeam())
					ended = !now.Before(p.stopAt)
				}
			}
			if !ended {
				continue
			}
			p.end = now
			if p.dir == 1 && r.cfg.Reverse {
				t.passes = append(t.passes, &sigPass{dir: -1, start: now})
				speed := -r.cfg.Speed
				cmds = append(cmds, func() { turret(id, speed) })
			} else {
				t.finished = true
				cmds = append(cmds, func() { turret(id, 0) })
			}
		}
		st.mu.Unlock()
		for _, c := range cmds {
			c()
		}
		if allDone {
			return
		}
	}
}

// finish stops everything and keeps the results.
func (st *signalTest) finish(r *sigRun) {
	r.cancel() // ends the IR and router loops, and the driving
	for _, id := range r.order {
		st.send(id, `{"cmd":"turret","speed":0}`)
		st.send(id, `{"cmd":"ir_emit_stop"}`)
	}
	if r.driveDone != nil {
		select {
		case <-r.driveDone: // tracks stopped
		case <-time.After(5 * time.Second):
		}
	}
	for _, id := range r.order {
		st.send(id, `{"cmd":"motors_off"}`)
	}
	st.mu.Lock()
	now := time.Now()
	for _, id := range r.order {
		t := r.tanks[id]
		t.frames = st.rl.get(id).stopRecording()
		if n := len(t.passes); n > 0 && t.passes[n-1].end.IsZero() {
			t.passes[n-1].end = now
		}
	}
	if r.phase == "stopping" {
		r.phase = "stopped"
	} else {
		r.phase = "done"
	}
	r.ended = now
	r.file = st.save(r)
	snap := st.snapshotLocked(r)
	st.mu.Unlock()

	for _, t := range snap.Tanks {
		log.Printf("[SignalTest] %s: %s", t.Name, t.Summary)
	}
	if st.onDone != nil {
		st.onDone()
	}
}

// irLoop: partners take turns, one lit while the other listens.
func (st *signalTest) irLoop(ctx context.Context, r *sigRun) {
	var lowGroup, highGroup []string // 1-5 and the rest
	for _, id := range r.order {
		if t := r.tanks[id]; t.num >= 1 && t.num <= 5 {
			lowGroup = append(lowGroup, id)
		} else {
			highGroup = append(highGroup, id)
		}
	}
	turns := [][2][]string{{lowGroup, highGroup}, {highGroup, lowGroup}}
	for {
		for _, tn := range turns {
			lit, listen := tn[0], tn[1]
			for _, id := range lit {
				st.send(id, `{"cmd":"ir_emit_left"}`)
			}
			if !sleepCtx(ctx, 40*time.Millisecond) {
				return
			}
			mid := time.Now().Add(sigListenMs / 2 * time.Millisecond)
			st.mu.Lock()
			for _, id := range listen {
				r.listenAt[id] = mid
			}
			st.mu.Unlock()
			for _, id := range listen {
				st.send(id, fmt.Sprintf(`{"cmd":"ir_listen_window","ms":%d}`, sigListenMs))
			}
			if !sleepCtx(ctx, (sigListenMs+110)*time.Millisecond) {
				return
			}
			for _, id := range lit {
				st.send(id, `{"cmd":"ir_emit_stop"}`)
			}
			if !sleepCtx(ctx, 20*time.Millisecond) {
				return
			}
		}
	}
}

// irResult takes a listening window's result during a run. True if it was
// the test's (the game's own IR logic must not see it).
func (st *signalTest) irResult(listener string, mask int) bool {
	if st == nil {
		return false
	}
	st.mu.Lock()
	defer st.mu.Unlock()
	r := st.run
	if r == nil {
		return false
	}
	at, ok := r.listenAt[listener]
	if !ok {
		return false // not a window the test asked for
	}
	delete(r.listenAt, listener)
	if !r.ended.IsZero() {
		return true // a late reply after the run
	}
	l := r.tanks[listener]
	if l == nil || l.partner == "" || mask&sigFrontMask == 0 {
		return true
	}
	if p := r.tanks[l.partner]; p != nil {
		p.hits = append(p.hits, at)
	}
	return true
}

// routerLoop reads the router's view of each tank several times a second.
func (st *signalTest) routerLoop(ctx context.Context, r *sigRun) {
	if st.iface == "" {
		st.setRouterNote(r, false, "off (-wifi-iface is empty)")
		return
	}
	idByMAC := map[string]string{}
	for _, id := range r.order {
		if mac := macFromTankID(id); mac != "" {
			idByMAC[mac] = id
		}
	}
	tick := time.NewTicker(sigRouterEvery)
	defer tick.Stop()
	for {
		cctx, cancel := context.WithTimeout(ctx, time.Second)
		out, err := exec.CommandContext(cctx, "iwinfo", st.iface, "assoclist").Output()
		cancel()
		if ctx.Err() != nil {
			return
		}
		if errors.Is(err, exec.ErrNotFound) {
			st.setRouterNote(r, false, "not available: the hub isn't running on the router")
			return
		}
		if err != nil {
			st.setRouterNote(r, false, "not available ("+err.Error()+")")
			return
		}
		now := time.Now()
		samples := parseAssocList(string(out))
		st.mu.Lock()
		r.routerOK = true
		for mac, s := range samples {
			if t := r.tanks[idByMAC[mac]]; t != nil {
				s.at = now
				t.router = append(t.router, s)
			}
		}
		st.mu.Unlock()
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
		}
	}
}

func (st *signalTest) setRouterNote(r *sigRun, ok bool, note string) {
	st.mu.Lock()
	r.routerOK, r.routerNote = ok, note
	st.mu.Unlock()
}

// macFromTankID: a tank's id is its MAC address with the bytes reversed
// (Desert-01: id 9CF218697090, MAC 90:70:69:18:F2:9C).
func macFromTankID(id string) string {
	if len(id) != 12 {
		return ""
	}
	parts := make([]string, 6)
	for i := 0; i < 6; i++ {
		parts[5-i] = strings.ToUpper(id[i*2 : i*2+2])
	}
	return strings.Join(parts, ":")
}

var (
	assocStationRe = regexp.MustCompile(`^([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5})\s+(-?\d+) dBm`)
	assocRxRe      = regexp.MustCompile(`^\s*RX:\s*([\d.]+) MBit/s`)
)

// parseAssocList reads `iwinfo <iface> assoclist`: per station, its signal
// and the rate the router receives it at.
func parseAssocList(out string) map[string]sigRouter {
	res := map[string]sigRouter{}
	cur := ""
	sc := bufio.NewScanner(strings.NewReader(out))
	for sc.Scan() {
		line := sc.Text()
		if m := assocStationRe.FindStringSubmatch(line); m != nil {
			cur = strings.ToUpper(m[1])
			dbm, _ := strconv.Atoi(m[2])
			res[cur] = sigRouter{dbm: dbm}
			continue
		}
		if m := assocRxRe.FindStringSubmatch(line); m != nil && cur != "" {
			s := res[cur]
			s.rxMbit, _ = strconv.ParseFloat(m[1], 64)
			res[cur] = s
		}
	}
	return res
}

// ── Beams and angles ─────────────────────────────────────────────────────────

type sigBeam struct{ first, last time.Time }

func (b sigBeam) mid() time.Time { return b.first.Add(b.last.Sub(b.first) / 2) }

// beams groups the partner's hits during a pass into passes of the barrel
// across it. Each beam's middle is a moment the turret faced its partner.
func (t *sigTank) beams(p *sigPass, until time.Time) []sigBeam {
	var out []sigBeam
	end := p.end
	if end.IsZero() {
		end = until
	}
	for _, h := range t.hits {
		if h.Before(p.start) || h.After(end) {
			continue
		}
		if n := len(out); n > 0 && h.Sub(out[n-1].last) <= sigRunGap {
			out[n-1].last = h
			continue
		}
		out = append(out, sigBeam{first: h, last: h})
	}
	return out
}

// turnBeams are a pass's beams that mark completed turns: not the one
// already in view as the pass began.
func (t *sigTank) turnBeams(p *sigPass, until time.Time) []sigBeam {
	var out []sigBeam
	for _, b := range t.beams(p, until) {
		if b.first.Sub(p.start) < sigStartIgnore {
			continue
		}
		out = append(out, b)
	}
	return out
}

// turnCount counts a pass's turns from its beams: the gap from the pass's
// start (or the last beam) to each beam, in turns. A beam the partner
// missed (the turret swept past between two listening windows) shows as a
// gap of about two turns, and counts as two. at is when the last counted
// beam was first seen.
func (t *sigTank) turnCount(p *sigPass, until time.Time) (count int, at time.Time) {
	per := t.passPeriod(p, until)
	prev := p.start
	for _, b := range t.turnBeams(p, until) {
		gap := b.first.Sub(prev).Seconds()
		if per <= 0 {
			per = gap // the first turn sets the pace
		}
		count += max(1, int(math.Round(gap/per)))
		at, prev = b.first, b.mid()
	}
	return count, at
}

// finalBeamStart: when the beam of the pass's last turn was first seen.
func (t *sigTank) finalBeamStart(p *sigPass, turns int) (time.Time, bool) {
	per := t.passPeriod(p, time.Now())
	prev, count := p.start, 0
	for _, b := range t.turnBeams(p, time.Now()) {
		gap := b.first.Sub(prev).Seconds()
		if per <= 0 {
			per = gap
		}
		count += max(1, int(math.Round(gap/per)))
		if count >= turns {
			return b.first, true
		}
		prev = b.mid()
	}
	return time.Time{}, false
}

// period: the tank's time per turn: the gaps between one beam's first
// sighting and the next one's, in the same pass (the last turn's beam counts:
// only its end is cut short). A missed beam only makes a gap longer and a
// stray reflection shorter, so it takes the shortest gap that isn't much
// shorter than the typical one. Failing that, the first pass's start to its
// first beam (the tanks start lined up). 0 = none yet.
func (t *sigTank) period() float64 {
	var gaps []float64
	now := time.Now()
	for _, p := range t.passes {
		bs := t.turnBeams(p, now)
		for i := 1; i < len(bs); i++ {
			if d := bs[i].first.Sub(bs[i-1].first).Seconds(); d >= sigMinTurn {
				gaps = append(gaps, d)
			}
		}
	}
	if len(gaps) > 0 {
		typical, best := median(gaps), 0.0
		for _, g := range gaps {
			if g >= 0.6*typical && (best == 0 || g < best) {
				best = g
			}
		}
		// The single-turn gaps (not ones spanning a missed beam), averaged.
		var single []float64
		for _, g := range gaps {
			if g >= best && g <= 1.4*best {
				single = append(single, g)
			}
		}
		return median(single)
	}
	if len(t.passes) > 0 {
		if bs := t.turnBeams(t.passes[0], now); len(bs) > 0 {
			return bs[0].mid().Sub(t.passes[0].start).Seconds()
		}
	}
	return 0
}

// passPeriod: the pace of one pass. A turret can turn at different speeds
// each way (Jungle-06, 21 October 2026: 42 s a turn one way, 30 s back), so
// each pass uses its own beam gaps when it has any, else the tank's pace.
func (t *sigTank) passPeriod(p *sigPass, until time.Time) float64 {
	bs := t.turnBeams(p, until)
	var gaps []float64
	for i := 1; i < len(bs); i++ {
		if d := bs[i].first.Sub(bs[i-1].first).Seconds(); d >= sigMinTurn {
			gaps = append(gaps, d)
		}
	}
	if len(gaps) == 0 {
		return t.period()
	}
	typical, best := median(gaps), 0.0
	for _, g := range gaps {
		if g >= 0.6*typical && (best == 0 || g < best) {
			best = g
		}
	}
	var single []float64
	for _, g := range gaps {
		if g >= best && g <= 1.4*best {
			single = append(single, g)
		}
	}
	return median(single)
}

// halfBeam: half the time a beam takes to sweep past, from the complete
// beams seen so far.
func (t *sigTank) halfBeam() time.Duration {
	var sum time.Duration
	n := 0
	for _, p := range t.passes {
		for _, b := range t.completeBeams(p, time.Now()) {
			sum += b.last.Sub(b.first)
			n++
		}
	}
	if n == 0 {
		return sigDefaultHalf
	}
	return sum / time.Duration(2*n)
}

// completeBeams: a pass's turn beams that swept fully past. Not the last
// turn's (the turret stopped inside it), nor one still in view.
func (t *sigTank) completeBeams(p *sigPass, until time.Time) []sigBeam {
	var out []sigBeam
	for _, b := range t.turnBeams(p, until) {
		if !p.finalFirst.IsZero() && !b.first.Before(p.finalFirst) {
			break
		}
		if p.end.IsZero() && until.Sub(b.last) <= sigRunGap {
			break // may still be growing
		}
		out = append(out, b)
	}
	return out
}

// rawMarks: the moments the turret faced its partner (0°) during a pass:
// the middles of the complete beams.
func (t *sigTank) rawMarks(p *sigPass, until time.Time) []time.Time {
	var m []time.Time
	for _, b := range t.completeBeams(p, until) {
		m = append(m, b.mid())
	}
	return m
}

// marks: rawMarks with any missed beam filled in, so every gap is one turn.
func (t *sigTank) marks(p *sigPass, until time.Time) []time.Time {
	return fillMissed(t.rawMarks(p, until), t.passPeriod(p, until))
}

// fillMissed puts a mark into each gap of about n turns (n ≥ 2), evenly.
func fillMissed(m []time.Time, per float64) []time.Time {
	if per <= 0 || len(m) < 2 {
		return m
	}
	out := []time.Time{m[0]}
	for i := 1; i < len(m); i++ {
		gap := m[i].Sub(m[i-1])
		for j, k := 1, int(math.Round(gap.Seconds()/per)); j < k; j++ {
			out = append(out, m[i-1].Add(gap*time.Duration(j)/time.Duration(k)))
		}
		out = append(out, m[i])
	}
	return out
}

// angleFunc maps a time to the turret's angle (0-360, 0 = facing forward).
// Between marks it interpolates; before the first and after the last it
// carries on at per seconds a turn (the tank's own pace). False without a
// mark or a pace.
func angleFunc(dir int, marks []time.Time, per float64) func(time.Time) (float64, bool) {
	return func(at time.Time) (float64, bool) {
		if len(marks) == 0 {
			return 0, false
		}
		first, last := marks[0], marks[len(marks)-1]
		var frac float64
		switch {
		case at.Before(first):
			if per <= 0 {
				return 0, false
			}
			frac = -first.Sub(at).Seconds() / per
		case at.After(last):
			if per <= 0 {
				return 0, false
			}
			frac = at.Sub(last).Seconds() / per
		case len(marks) == 1:
			frac = 0
		default:
			i := sort.Search(len(marks), func(i int) bool { return marks[i].After(at) }) - 1
			i = max(0, min(i, len(marks)-2))
			seg := marks[i+1].Sub(marks[i]).Seconds()
			if seg <= 0 {
				return 0, false
			}
			frac = at.Sub(marks[i]).Seconds() / seg
		}
		frac -= math.Floor(frac)
		a := frac * 360
		if dir < 0 {
			a = 360 - a
		}
		return math.Mod(a, 360), true
	}
}

// ── Results ──────────────────────────────────────────────────────────────────

type sigBin struct {
	Fps    *float64 `json:"fps,omitempty"`    // frames reaching the hub per second
	GapMs  *float64 `json:"gapMs,omitempty"`  // longest wait between two frames
	Dbm    *float64 `json:"dbm,omitempty"`    // router's view: signal
	RxMbit *float64 `json:"rxMbit,omitempty"` // router's view: receive rate
}

type sigPassView struct {
	Dir   int      `json:"dir"`
	TurnS float64  `json:"turnS"` // this pass's seconds per turn
	Bins  []sigBin `json:"bins"`
}

type sigTankView struct {
	ID       string        `json:"id"`
	Name     string        `json:"name"`
	Partner  string        `json:"partner"`
	Ref      string        `json:"ref"`      // ok, estimated (no 0° marks), none
	Turns    int           `json:"turns"`    // completed turns seen
	TurnS    float64       `json:"turnS"`    // average seconds per turn
	Progress string        `json:"progress"` // while running
	Passes   []sigPassView `json:"passes"`
	Summary  string        `json:"summary"`
	WorstFps *float64      `json:"worstFps,omitempty"`
	WorstAt  int           `json:"worstAt"` // angle of the worst 10°
	MedFps   *float64      `json:"medFps,omitempty"`
	DbmMin   *float64      `json:"dbmMin,omitempty"`
	DbmMax   *float64      `json:"dbmMax,omitempty"`
}

type sigSnapshot struct {
	State      string        `json:"state"` // none, warm-up, turning, stopping, done, stopped
	Running    bool          `json:"running"`
	StartedAt  int64         `json:"startedAt,omitempty"` // unix ms
	ElapsedS   float64       `json:"elapsedS"`
	Note       string        `json:"note,omitempty"`
	RouterOK   bool          `json:"routerOk"`
	RouterNote string        `json:"routerNote,omitempty"`
	Settings   any           `json:"settings,omitempty"`
	Tanks      []sigTankView `json:"tanks"`
	Drive      bool          `json:"drive"`          // the tanks drove back and forth too (Settings.Drive)
	Run        string        `json:"run,omitempty"`  // its file name, from the start (comments are kept by it)
	File       string        `json:"file,omitempty"` // once saved
	Comment    string        `json:"comment,omitempty"`
}

func (st *signalTest) snapshot() sigSnapshot {
	st.mu.Lock()
	defer st.mu.Unlock()
	if st.run == nil && !st.triedLatest {
		// After a restart the last run is the newest saved one.
		st.triedLatest = true
		if runs := st.listRunsLocked(); len(runs) > 0 {
			if r, err := loadRunFile(filepath.Join(st.dir, runs[0].File)); err == nil {
				st.run = r
			}
		}
	}
	if st.run == nil {
		return sigSnapshot{State: "none", Tanks: []sigTankView{}}
	}
	snap := st.snapshotLocked(st.run)
	snap.Comment = st.comments[snap.Run]
	return snap
}

func ptr(v float64) *float64 { return &v }

func (st *signalTest) snapshotLocked(r *sigRun) sigSnapshot {
	now := time.Now()
	end := r.ended
	if end.IsZero() {
		end = now
	}
	snap := sigSnapshot{State: r.phase, Running: r.ended.IsZero(), StartedAt: r.started.UnixMilli(),
		ElapsedS: end.Sub(r.started).Seconds(), Note: r.note, RouterOK: r.routerOK, RouterNote: r.routerNote,
		Settings: r.cfg, Drive: r.cfg.Drive, Run: r.name, File: r.file, Tanks: []sigTankView{}}

	// The fleet's typical turn time, for tanks without a pace of their own.
	var paces []float64
	for _, id := range r.order {
		if per := r.tanks[id].period(); per > 0 {
			paces = append(paces, per)
		}
	}
	fleetPace := median(paces)

	for _, id := range r.order {
		t := r.tanks[id]
		frames := t.frames
		if r.ended.IsZero() {
			frames = st.rl.get(id).recorded()
		}
		v := sigTankView{ID: t.id, Name: t.name, Ref: "ok", Passes: []sigPassView{}}
		if p := r.tanks[t.partner]; p != nil {
			v.Partner = p.name
		}
		pace := t.period()
		if pace <= 0 {
			pace = fleetPace
		}
		v.TurnS = pace
		for _, p := range t.passes {
			m := t.marks(p, end)
			n, _ := t.turnCount(p, end)
			v.Turns += n
			if len(m) == 0 {
				// No 0° mark this pass: assume it began facing forward.
				m = []time.Time{p.start}
				v.Ref = "estimated"
			}
			passPace := t.passPeriod(p, end)
			if passPace <= 0 {
				passPace = pace
			}
			v.Passes = append(v.Passes, sigPassView{Dir: p.dir, TurnS: passPace,
				Bins: binPass(p, end, angleFunc(p.dir, m, passPace), frames, t.router)})
		}
		if len(t.passes) == 0 || pace <= 0 {
			v.Ref = "none"
		}
		if snap.Running {
			if n := len(t.passes); n > 0 {
				dirName := map[int]string{1: "first way", -1: "back"}[t.passes[n-1].dir]
				done, _ := t.turnCount(t.passes[n-1], end)
				v.Progress = fmt.Sprintf("%s, %d of %d turns", dirName, min(done, r.cfg.Turns), r.cfg.Turns)
				if t.finished {
					v.Progress = "finished"
				}
			}
		}
		summarise(&v)
		snap.Tanks = append(snap.Tanks, v)
	}
	return snap
}

// binPass sorts a pass's frames and router readings into 10° bins.
func binPass(p *sigPass, end time.Time, angle func(time.Time) (float64, bool), frames []time.Time, router []sigRouter) []sigBin {
	pEnd := p.end
	if pEnd.IsZero() {
		pEnd = end
	}
	bin := func(at time.Time) int {
		a, ok := angle(at)
		if !ok {
			return -1
		}
		return min(int(a/10), sigBins-1)
	}
	var dwell [sigBins]float64 // seconds the turret spent in each bin
	const step = 50 * time.Millisecond
	for at := p.start; at.Before(pEnd); at = at.Add(step) {
		if b := bin(at); b >= 0 {
			dwell[b] += step.Seconds()
		}
	}
	var count [sigBins]int
	var gap [sigBins]float64
	var prev time.Time
	for _, f := range frames {
		if f.Before(p.start) || f.After(pEnd) {
			prev = f
			continue
		}
		if b := bin(f); b >= 0 {
			count[b]++
			if !prev.IsZero() && !prev.Before(p.start) {
				if b2 := bin(prev.Add(f.Sub(prev) / 2)); b2 >= 0 {
					gap[b2] = max(gap[b2], float64(f.Sub(prev).Milliseconds()))
				}
			}
		}
		prev = f
	}
	var dbmSum, rxSum [sigBins]float64
	var rn [sigBins]int
	for _, s := range router {
		if s.at.Before(p.start) || s.at.After(pEnd) {
			continue
		}
		if b := bin(s.at); b >= 0 {
			dbmSum[b] += float64(s.dbm)
			rxSum[b] += s.rxMbit
			rn[b]++
		}
	}
	out := make([]sigBin, sigBins)
	for i := range out {
		if dwell[i] >= 0.2 {
			out[i].Fps = ptr(math.Round(float64(count[i])/dwell[i]*10) / 10)
			out[i].GapMs = ptr(gap[i])
		}
		if rn[i] > 0 {
			out[i].Dbm = ptr(math.Round(dbmSum[i]/float64(rn[i])*10) / 10)
			out[i].RxMbit = ptr(math.Round(rxSum[i]/float64(rn[i])*10) / 10)
		}
	}
	return out
}

func summarise(v *sigTankView) {
	var fps, dbm []float64
	worst, worstAt := math.Inf(1), 0
	for _, p := range v.Passes {
		for i, b := range p.Bins {
			if b.Fps != nil {
				fps = append(fps, *b.Fps)
				if *b.Fps < worst {
					worst, worstAt = *b.Fps, i*10
				}
			}
			if b.Dbm != nil {
				dbm = append(dbm, *b.Dbm)
			}
		}
	}
	parts := []string{}
	if len(fps) > 0 {
		v.MedFps, v.WorstFps, v.WorstAt = ptr(median(fps)), ptr(worst), worstAt
		parts = append(parts, fmt.Sprintf("video %.0f fps typical, worst %.1f fps at %d-%d°", median(fps), worst, worstAt, worstAt+10))
	}
	if len(dbm) > 0 {
		sort.Float64s(dbm)
		v.DbmMin, v.DbmMax = ptr(dbm[0]), ptr(dbm[len(dbm)-1])
		parts = append(parts, fmt.Sprintf("router hears it at %.0f to %.0f dBm", dbm[0], dbm[len(dbm)-1]))
	}
	var paces []string
	for _, p := range v.Passes {
		if p.TurnS > 0 {
			paces = append(paces, fmt.Sprintf("%.1f", p.TurnS))
		}
	}
	if len(paces) > 0 {
		parts = append(parts, strings.Join(paces, " / ")+" s per turn")
	}
	switch v.Ref {
	case "estimated":
		parts = append(parts, "no 0° marks: angles estimated")
	case "none":
		parts = append(parts, "no angle reference")
	}
	v.Summary = strings.Join(parts, "; ")
}

// ── HTTP ─────────────────────────────────────────────────────────────────────

// snapshotFor: the run a request asks for: ?run=<saved file>, else the
// current or last run.
func (st *signalTest) snapshotFor(r *http.Request) (sigSnapshot, bool) {
	if file := r.URL.Query().Get("run"); file != "" {
		return st.savedSnapshot(file)
	}
	return st.snapshot(), true
}

// handle: GET /api/sigtest[?run=<file>] — a run, binned by angle.
func (st *signalTest) handle(w http.ResponseWriter, r *http.Request) {
	snap, ok := st.snapshotFor(r)
	if !ok {
		http.Error(w, "no such run", http.StatusNotFound)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(snap)
}

// handleRuns: GET /api/sigtest/runs — the saved runs, newest first.
func (st *signalTest) handleRuns(w http.ResponseWriter, r *http.Request) {
	st.mu.Lock()
	runs := st.listRunsLocked()
	st.mu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(runs)
}

// sigHistRun is one saved run for the page's averages: each tank's bins
// with both directions merged.
type sigHistRun struct {
	File    string        `json:"file"`
	Started int64         `json:"started"` // unix ms
	Drive   bool          `json:"drive"`   // the tanks drove back and forth too
	Comment string        `json:"comment,omitempty"`
	Tanks   []sigHistTank `json:"tanks"`
}

type sigHistTank struct {
	ID   string   `json:"id"`
	Name string   `json:"name"`
	Ref  string   `json:"ref"` // ok, estimated, none: only "ok" runs have trustworthy angles
	Bins []sigBin `json:"bins"`
}

// handleHistory: GET /api/sigtest/history — every saved run, oldest first,
// each tank's passes merged, for averaging over a span of dates.
func (st *signalTest) handleHistory(w http.ResponseWriter, r *http.Request) {
	st.mu.Lock()
	runs := st.listRunsLocked()
	st.mu.Unlock()
	out := []sigHistRun{}
	for i := len(runs) - 1; i >= 0; i-- {
		snap, ok := st.savedSnapshot(runs[i].File)
		if !ok {
			continue
		}
		h := sigHistRun{File: runs[i].File, Started: snap.StartedAt, Drive: snap.Drive, Comment: snap.Comment, Tanks: []sigHistTank{}}
		for _, t := range snap.Tanks {
			h.Tanks = append(h.Tanks, sigHistTank{ID: t.ID, Name: t.Name, Ref: t.Ref, Bins: mergePasses(t.Passes)})
		}
		out = append(out, h)
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(out)
}

// mergePasses averages a tank's passes (both directions) bin by bin.
func mergePasses(passes []sigPassView) []sigBin {
	out := make([]sigBin, sigBins)
	avg := func(get func(sigBin) *float64, i int) *float64 {
		sum, n := 0.0, 0
		for _, p := range passes {
			if i < len(p.Bins) {
				if v := get(p.Bins[i]); v != nil {
					sum += *v
					n++
				}
			}
		}
		if n == 0 {
			return nil
		}
		return ptr(math.Round(sum/float64(n)*10) / 10)
	}
	for i := range out {
		out[i] = sigBin{
			Fps:    avg(func(b sigBin) *float64 { return b.Fps }, i),
			GapMs:  avg(func(b sigBin) *float64 { return b.GapMs }, i),
			Dbm:    avg(func(b sigBin) *float64 { return b.Dbm }, i),
			RxMbit: avg(func(b sigBin) *float64 { return b.RxMbit }, i),
		}
	}
	return out
}

// handleCSV: GET /api/sigtest.csv[?run=<file>] — the same, one row per
// tank, pass and 10°.
func (st *signalTest) handleCSV(w http.ResponseWriter, r *http.Request) {
	snap, ok := st.snapshotFor(r)
	if !ok {
		http.Error(w, "no such run", http.StatusNotFound)
		return
	}
	name := "signal-test.csv"
	if snap.StartedAt > 0 {
		name = "signal-test-" + time.UnixMilli(snap.StartedAt).Format("20060102-150405") + ".csv"
	}
	w.Header().Set("Content-Type", "text/csv; charset=utf-8")
	w.Header().Set("Content-Disposition", `attachment; filename="`+name+`"`)
	w.Header().Set("Cache-Control", "no-store")
	bw := bufio.NewWriter(w)
	defer bw.Flush()
	fmt.Fprintln(bw, "tank,partner,reference,pass,angle_from,angle_to,video_fps,worst_gap_ms,router_dbm,router_rx_mbit,driving")
	driving := "no"
	if snap.Drive {
		driving = "yes"
	}
	f := func(p *float64) string {
		if p == nil {
			return ""
		}
		return strconv.FormatFloat(*p, 'f', 1, 64)
	}
	for _, t := range snap.Tanks {
		for _, p := range t.Passes {
			pass := "first"
			if p.Dir < 0 {
				pass = "back"
			}
			for i, b := range p.Bins {
				fmt.Fprintf(bw, "%s,%s,%s,%s,%d,%d,%s,%s,%s,%s,%s\n", csvField(t.Name), csvField(t.Partner), t.Ref, pass,
					i*10, i*10+10, f(b.Fps), f(b.GapMs), f(b.Dbm), f(b.RxMbit), driving)
			}
		}
	}
}

// ── Saved runs ───────────────────────────────────────────────────────────────

// sigKeepRuns: saved runs kept; older ones are deleted (each is about 1 MB
// for ten tanks).
const sigKeepRuns = 30

var sigRunFileRe = regexp.MustCompile(`^thundergeddon-sigtest-\d{8}-\d{6}\.json$`)

// sigRunFile is a finished run's raw data (marks, hits, router readings,
// frame times), so its charts can be worked out again later.
type sigRunFile struct {
	Started  int64              `json:"started"`
	Ended    int64              `json:"ended"`
	Phase    string             `json:"phase"`
	Settings SignalTestSettings `json:"settings"`
	Tanks    []sigTankFile      `json:"tanks"`
}

type sigTankFile struct {
	ID      string        `json:"id"`
	Name    string        `json:"name"`
	Partner string        `json:"partner"`
	Passes  []sigPassFile `json:"passes"`
	Hits    []int64       `json:"hits"`
	Router  [][3]float64  `json:"router"` // unix ms, dBm, rx Mbit/s
	Frames  []int64       `json:"frames"`
}

type sigPassFile struct {
	Dir      int     `json:"dir"`
	StartMs  int64   `json:"start"`
	EndMs    int64   `json:"end"`
	TimedOut bool    `json:"timedOut"`
	Marks    []int64 `json:"marks"` // as shown when it ran (for reading by hand)
}

// sigRunInfo is a saved run's headline, for the page's list.
type sigRunInfo struct {
	File    string   `json:"file"`
	Started int64    `json:"started"` // unix ms
	Phase   string   `json:"phase"`
	Tanks   []string `json:"tanks"`
	Drive   bool     `json:"drive"` // the tanks drove back and forth too
	Comment string   `json:"comment,omitempty"`
}

// sigCommentsFile holds the host's comments on runs ("bigger antennas",
// "tanks swapped places"...), by run file name.
const sigCommentsFile = "comments.json"

const sigMaxComment = 300

func sigRunName(started time.Time) string {
	return "thundergeddon-sigtest-" + started.Format("20060102-150405") + ".json"
}

// setComment keeps the host's comment on a run ("" removes it).
func (st *signalTest) setComment(run, text string) error {
	if st == nil || !sigRunFileRe.MatchString(run) {
		return fmt.Errorf("no such run")
	}
	text = strings.TrimSpace(text)
	if r := []rune(text); len(r) > sigMaxComment {
		text = string(r[:sigMaxComment])
	}
	st.mu.Lock()
	defer st.mu.Unlock()
	if text == "" {
		delete(st.comments, run)
	} else {
		st.comments[run] = text
	}
	return st.saveCommentsLocked()
}

func (st *signalTest) saveCommentsLocked() error {
	if st.dir == "" {
		return nil
	}
	b, err := json.MarshalIndent(st.comments, "", "  ")
	if err != nil {
		return err
	}
	path := filepath.Join(st.dir, sigCommentsFile)
	if err := os.WriteFile(path+".tmp", b, 0o644); err != nil {
		return err
	}
	return os.Rename(path+".tmp", path)
}

// save writes a finished run's raw data as JSON and drops the oldest runs
// beyond sigKeepRuns. A run stopped early isn't kept. Returns the file's
// name. Caller holds st.mu.
func (st *signalTest) save(r *sigRun) string {
	if st.dir == "" || r.phase != "done" {
		return ""
	}
	ms := func(ts []time.Time) []int64 {
		out := make([]int64, len(ts))
		for i, t := range ts {
			out[i] = t.UnixMilli()
		}
		return out
	}
	out := sigRunFile{Started: r.started.UnixMilli(), Ended: r.ended.UnixMilli(), Phase: r.phase, Settings: r.cfg}
	for _, id := range r.order {
		t := r.tanks[id]
		to := sigTankFile{ID: t.id, Name: t.name, Hits: ms(t.hits), Frames: ms(t.frames)}
		if p := r.tanks[t.partner]; p != nil {
			to.Partner = p.name
		}
		for _, p := range t.passes {
			to.Passes = append(to.Passes, sigPassFile{Dir: p.dir, StartMs: p.start.UnixMilli(), EndMs: p.end.UnixMilli(),
				TimedOut: p.timedOut, Marks: ms(t.marks(p, r.ended))})
		}
		for _, s := range t.router {
			to.Router = append(to.Router, [3]float64{float64(s.at.UnixMilli()), float64(s.dbm), s.rxMbit})
		}
		out.Tanks = append(out.Tanks, to)
	}
	b, err := json.Marshal(out)
	if err != nil {
		return ""
	}
	name := r.name
	path := filepath.Join(st.dir, name)
	if err := os.WriteFile(path, b, 0o644); err != nil {
		log.Printf("[SignalTest] can't save %s: %v", path, err)
		return ""
	}
	log.Printf("[SignalTest] saved %s", path)
	runs := st.listRunsLocked()
	for _, old := range runs[min(len(runs), sigKeepRuns):] {
		_ = os.Remove(filepath.Join(st.dir, old.File))
		delete(st.infos, old.File)
		delete(st.views, old.File)
		if _, ok := st.comments[old.File]; ok {
			delete(st.comments, old.File)
			_ = st.saveCommentsLocked()
		}
	}
	return name
}

// adopt copies saved runs from an older folder into st.dir.
func (st *signalTest) adopt(dir string) {
	if dir == "" || filepath.Clean(dir) == filepath.Clean(st.dir) {
		return
	}
	entries, _ := os.ReadDir(dir)
	for _, e := range entries {
		if !sigRunFileRe.MatchString(e.Name()) {
			continue
		}
		dst := filepath.Join(st.dir, e.Name())
		if _, err := os.Stat(dst); err == nil {
			continue
		}
		if b, err := os.ReadFile(filepath.Join(dir, e.Name())); err == nil && os.WriteFile(dst, b, 0o644) == nil {
			log.Printf("[SignalTest] kept %s in %s", e.Name(), st.dir)
		}
	}
}

// listRunsLocked: the saved runs, newest first. Caller holds st.mu.
func (st *signalTest) listRunsLocked() []sigRunInfo {
	runs := []sigRunInfo{}
	if st.dir == "" {
		return runs
	}
	entries, _ := os.ReadDir(st.dir)
	for _, e := range entries {
		name := e.Name()
		if !sigRunFileRe.MatchString(name) {
			continue
		}
		info, ok := st.infos[name]
		if !ok {
			var head struct {
				Started  int64  `json:"started"`
				Phase    string `json:"phase"`
				Settings struct {
					Drive bool `json:"drive"`
				} `json:"settings"`
				Tanks []struct {
					Name string `json:"name"`
				} `json:"tanks"`
			}
			b, err := os.ReadFile(filepath.Join(st.dir, name))
			if err != nil || json.Unmarshal(b, &head) != nil {
				continue
			}
			if head.Phase != "done" {
				// Stopped early (kept by older versions): only finished runs count.
				_ = os.Remove(filepath.Join(st.dir, name))
				continue
			}
			info = sigRunInfo{File: name, Started: head.Started, Phase: head.Phase, Drive: head.Settings.Drive, Tanks: []string{}}
			for _, t := range head.Tanks {
				info.Tanks = append(info.Tanks, t.Name)
			}
			sort.Slice(info.Tanks, func(i, j int) bool { return naturalLess(info.Tanks[i], info.Tanks[j]) })
			st.infos[name] = info
		}
		info.Comment = st.comments[name]
		runs = append(runs, info)
	}
	sort.Slice(runs, func(i, j int) bool { return runs[i].File > runs[j].File })
	return runs
}

// savedSnapshot: a saved run, binned as it was shown.
func (st *signalTest) savedSnapshot(file string) (sigSnapshot, bool) {
	if !sigRunFileRe.MatchString(file) || st.dir == "" {
		return sigSnapshot{}, false
	}
	st.mu.Lock()
	defer st.mu.Unlock()
	v, ok := st.views[file]
	if !ok {
		r, err := loadRunFile(filepath.Join(st.dir, file))
		if err != nil {
			return sigSnapshot{}, false
		}
		v = st.snapshotLocked(r)
		st.views[file] = v
	}
	v.Comment = st.comments[file]
	return v, true
}

// loadRunFile reads a saved run back. Each pass's last beam (where the
// turret stopped) is found again from the hits, as it was while it ran.
func loadRunFile(path string) (*sigRun, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var f sigRunFile
	if err := json.Unmarshal(b, &f); err != nil {
		return nil, err
	}
	times := func(ms []int64) []time.Time {
		out := make([]time.Time, len(ms))
		for i, v := range ms {
			out[i] = time.UnixMilli(v)
		}
		return out
	}
	cfg := f.Settings
	cfg.clamp()
	r := &sigRun{started: time.UnixMilli(f.Started), ended: time.UnixMilli(f.Ended), phase: f.Phase, cfg: cfg,
		tanks: map[string]*sigTank{}, listenAt: map[string]time.Time{}, name: filepath.Base(path), file: filepath.Base(path), routerOK: true}
	byName := map[string]string{}
	partners := map[string]string{}
	for _, tf := range f.Tanks {
		t := &sigTank{id: tf.ID, name: tf.Name, num: tankNumber(tf.Name), hits: times(tf.Hits), frames: times(tf.Frames), finished: true}
		for _, p := range tf.Passes {
			t.passes = append(t.passes, &sigPass{dir: p.Dir, start: time.UnixMilli(p.StartMs), end: time.UnixMilli(p.EndMs), timedOut: p.TimedOut})
		}
		for _, s := range tf.Router {
			t.router = append(t.router, sigRouter{at: time.UnixMilli(int64(s[0])), dbm: int(s[1]), rxMbit: s[2]})
		}
		r.tanks[t.id] = t
		r.order = append(r.order, t.id)
		byName[t.name] = t.id
		partners[t.id] = tf.Partner
	}
	for _, t := range r.tanks {
		t.partner = byName[partners[t.id]]
		for _, p := range t.passes {
			if first, ok := t.finalBeamStart(p, cfg.Turns); ok && !p.timedOut {
				p.finalFirst = first
			}
		}
		for _, p := range t.passes {
			if !p.finalFirst.IsZero() {
				p.stopAt = p.finalFirst.Add(t.halfBeam())
			}
		}
	}
	return r, nil
}
