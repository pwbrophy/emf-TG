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
	sigBins        = 36                      // 10° each
	sigFrontMask   = 0x83                    // N, NE, NW sensors: the partner straight ahead
	sigListenMs    = 250                     // each listening window
	sigWarmup      = 4 * time.Second         // cameras start, motors on
	sigRunGap      = 1500 * time.Millisecond // hits further apart start a new pass of the beam
	sigStartIgnore = 2 * time.Second         // a beam already in view as a pass begins is the start, not a turn
	sigRouterEvery = 250 * time.Millisecond
	sigDefaultHalf = 400 * time.Millisecond // half a beam's width in time, before one is measured
)

type signalTest struct {
	tanks  tankControl
	rl     *relay
	iface  string // the router's Wi-Fi interface for its view of the tanks; "" = none
	dir    string // where finished runs are saved
	onDone func() // called (without st.mu) when a run ends: cameras back to normal

	mu  sync.Mutex
	run *sigRun // the current or last run
}

func newSignalTest(tanks tankControl, rl *relay, iface, dir string) *signalTest {
	return &signalTest{tanks: tanks, rl: rl, iface: iface, dir: dir}
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
	ctx, cancel := context.WithCancel(context.Background())
	r := &sigRun{started: time.Now(), phase: "warm-up", cfg: cfg, cancel: cancel,
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
	log.Printf("[SignalTest] starting with %d tanks: speed %.2f, %d turns, reverse %v", len(r.order), cfg.Speed, cfg.Turns, cfg.Reverse)
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
	r.cancel() // ends the IR and router loops
	for _, id := range r.order {
		st.send(id, `{"cmd":"turret","speed":0}`)
		st.send(id, `{"cmd":"ir_emit_stop"}`)
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
	per := t.period()
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
	per := t.period()
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
			if d := bs[i].first.Sub(bs[i-1].first).Seconds(); d > 1 {
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
	return fillMissed(t.rawMarks(p, until), t.period())
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
	Dir  int      `json:"dir"`
	Bins []sigBin `json:"bins"`
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
	File       string        `json:"file,omitempty"`
}

func (st *signalTest) snapshot() sigSnapshot {
	st.mu.Lock()
	defer st.mu.Unlock()
	if st.run == nil {
		return sigSnapshot{State: "none", Tanks: []sigTankView{}}
	}
	return st.snapshotLocked(st.run)
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
		Settings: r.cfg, File: r.file, Tanks: []sigTankView{}}

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
			v.Passes = append(v.Passes, sigPassView{Dir: p.dir, Bins: binPass(p, end, angleFunc(p.dir, m, pace), frames, t.router)})
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
	if v.TurnS > 0 {
		parts = append(parts, fmt.Sprintf("%.1f s per turn", v.TurnS))
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

// handle: GET /api/sigtest — the current or last run, binned by angle.
func (st *signalTest) handle(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(st.snapshot())
}

// handleCSV: GET /api/sigtest.csv — the same, one row per tank, pass and 10°.
func (st *signalTest) handleCSV(w http.ResponseWriter, r *http.Request) {
	snap := st.snapshot()
	w.Header().Set("Content-Type", "text/csv; charset=utf-8")
	w.Header().Set("Content-Disposition", `attachment; filename="signal-test.csv"`)
	w.Header().Set("Cache-Control", "no-store")
	bw := bufio.NewWriter(w)
	defer bw.Flush()
	fmt.Fprintln(bw, "tank,partner,reference,pass,angle_from,angle_to,video_fps,worst_gap_ms,router_dbm,router_rx_mbit")
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
				fmt.Fprintf(bw, "%s,%s,%s,%s,%d,%d,%s,%s,%s,%s\n", csvField(t.Name), csvField(t.Partner), t.Ref, pass,
					i*10, i*10+10, f(b.Fps), f(b.GapMs), f(b.Dbm), f(b.RxMbit))
			}
		}
	}
}

// save writes the run's raw data (marks, hits, router readings, frame
// times) as JSON next to the video log, for later comparison. Caller holds
// st.mu.
func (st *signalTest) save(r *sigRun) string {
	if st.dir == "" {
		return ""
	}
	ms := func(ts []time.Time) []int64 {
		out := make([]int64, len(ts))
		for i, t := range ts {
			out[i] = t.UnixMilli()
		}
		return out
	}
	type passOut struct {
		Dir      int     `json:"dir"`
		StartMs  int64   `json:"start"`
		EndMs    int64   `json:"end"`
		TimedOut bool    `json:"timedOut"`
		Marks    []int64 `json:"marks"`
	}
	type tankOut struct {
		ID      string       `json:"id"`
		Name    string       `json:"name"`
		Partner string       `json:"partner"`
		Passes  []passOut    `json:"passes"`
		Hits    []int64      `json:"hits"`
		Router  [][3]float64 `json:"router"` // unix ms, dBm, rx Mbit/s
		Frames  []int64      `json:"frames"`
	}
	out := struct {
		Started  int64              `json:"started"`
		Ended    int64              `json:"ended"`
		Phase    string             `json:"phase"`
		Settings SignalTestSettings `json:"settings"`
		Tanks    []tankOut          `json:"tanks"`
	}{r.started.UnixMilli(), r.ended.UnixMilli(), r.phase, r.cfg, nil}
	for _, id := range r.order {
		t := r.tanks[id]
		to := tankOut{ID: t.id, Name: t.name, Hits: ms(t.hits), Frames: ms(t.frames)}
		if p := r.tanks[t.partner]; p != nil {
			to.Partner = p.name
		}
		for _, p := range t.passes {
			to.Passes = append(to.Passes, passOut{Dir: p.dir, StartMs: p.start.UnixMilli(), EndMs: p.end.UnixMilli(),
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
	path := filepath.Join(st.dir, "thundergeddon-sigtest-"+r.started.Format("20060102-150405")+".json")
	if err := os.WriteFile(path, b, 0o644); err != nil {
		log.Printf("[SignalTest] can't save %s: %v", path, err)
		return ""
	}
	return path
}
