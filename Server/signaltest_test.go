package main

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"strings"
	"testing"
	"time"
)

func TestTankNumbersAndPartners(t *testing.T) {
	for name, want := range map[string]int{"Desert-01": 6, "Jungle-06": 1, "Desert-05": 10, "Jungle-10": 5, "Scout": 0, "Tank-11": 0} {
		if got := partnerNumber(tankNumber(name)); got != want {
			t.Errorf("partner of %s = %d, want %d", name, got, want)
		}
	}
	if got := macFromTankID("9CF218697090"); got != "90:70:69:18:F2:9C" {
		t.Errorf("mac = %s", got)
	}
}

func TestParseAssocList(t *testing.T) {
	out := `90:70:69:18:F2:9C  -16 dBm / unknown (SNR -16)  0 ms ago
	RX: 72.0 MBit/s, MCS 7, 20MHz                      0 Pkts.
	TX: 72.0 MBit/s, MCS 7, 20MHz                      0 Pkts.
	expected throughput: unknown

90:70:69:09:B6:D0  -41 dBm / unknown (SNR -41)  0 ms ago
	RX: 6.5 MBit/s, MCS 0, 20MHz                      0 Pkts.
	TX: 1.0 MBit/s                                     0 Pkts.
`
	got := parseAssocList(out)
	if a := got["90:70:69:18:F2:9C"]; a.dbm != -16 || a.rxMbit != 72 {
		t.Errorf("first station %+v", a)
	}
	if b := got["90:70:69:09:B6:D0"]; b.dbm != -41 || b.rxMbit != 6.5 {
		t.Errorf("second station %+v", b)
	}
}

// hitsFor simulates a turret turning at a steady 20 s a turn, its
// partner's front sensor seeing the beam within ±12° and listening every
// 0.6 s; skip leaves out beams (by turn number) the partner misses.
func hitsFor(t0 time.Time, turns int, skip ...int) []time.Time {
	const turn = 20.0
	var hits []time.Time
	for s := 0.0; s < float64(turns)*turn+0.5; s += 0.6 {
		a := math.Mod(s/turn*360, 360)
		n := int(math.Round(s / turn))
		missed := false
		for _, k := range skip {
			missed = missed || n == k
		}
		if (a < 12 || a > 348) && !missed {
			hits = append(hits, t0.Add(time.Duration(s*float64(time.Second))))
		}
	}
	return hits
}

// A pass of three turns: it stops in the third turn's beam, the beam
// middles land on the turns, and angles in between (and before the first
// mark, from the pace) come out right.
func TestMarksAndAngles(t *testing.T) {
	t0 := time.Date(2026, 10, 21, 12, 0, 0, 0, time.UTC)
	const turn = 20.0
	at := func(s float64) time.Time { return t0.Add(time.Duration(s * float64(time.Second))) }
	tk := &sigTank{hits: hitsFor(t0, 3)}
	p := &sigPass{dir: 1, start: t0}
	tk.passes = []*sigPass{p}

	first, ok := tk.finalBeamStart(p, 3)
	if !ok {
		t.Fatal("no final beam after 3 turns")
	}
	if d := first.Sub(at(3 * turn)).Seconds(); d > 0 || d < -1 {
		t.Errorf("final beam first seen %.2f s from the third turn", d)
	}
	p.finalFirst = first
	p.stopAt = first.Add(tk.halfBeam())
	p.end = p.stopAt
	if n, _ := tk.turnCount(p, p.end); n != 3 {
		t.Errorf("turns counted: %d", n)
	}

	m := tk.marks(p, p.end)
	if len(m) != 2 {
		t.Fatalf("marks: %d, want the first two turns' beam middles", len(m))
	}
	for i, want := range []float64{turn, 2 * turn} {
		if d := m[i].Sub(at(want)).Seconds(); math.Abs(d) > 0.7 {
			t.Errorf("mark %d at %.2f s, want %.0f s", i, m[i].Sub(t0).Seconds(), want)
		}
	}
	if per := tk.period(); math.Abs(per-turn) > 1 {
		t.Errorf("pace %.1f s a turn", per)
	}
	angle := angleFunc(1, m, tk.period())
	for _, s := range []float64{5, 25, 50} {
		a, ok := angle(at(s))
		want := math.Mod(s/turn*360, 360)
		if !ok || math.Abs(a-want) > 15 {
			t.Errorf("angle at %.0f s = %.0f (%v), want about %.0f", s, a, ok, want)
		}
	}
	back := angleFunc(-1, m, tk.period())
	if a, _ := back(at(25)); math.Abs(a-270) > 15 {
		t.Errorf("turning back, angle at 25 s = %.0f, want about 270", a)
	}
}

// The partner misses a beam: that gap counts as two turns, the pass still
// stops after the right number, and the missing mark is filled in.
func TestMissedBeam(t *testing.T) {
	t0 := time.Date(2026, 10, 21, 12, 0, 0, 0, time.UTC)
	at := func(s float64) time.Time { return t0.Add(time.Duration(s * float64(time.Second))) }
	tk := &sigTank{hits: hitsFor(t0, 5, 3)} // the third turn's beam is never seen
	p := &sigPass{dir: 1, start: t0}
	tk.passes = []*sigPass{p}
	first, ok := tk.finalBeamStart(p, 5)
	if !ok || first.Sub(at(100)).Seconds() < -1 || first.After(at(100)) {
		t.Fatalf("final beam: %v %v", ok, first.Sub(t0))
	}
	p.finalFirst, p.stopAt = first, first.Add(tk.halfBeam())
	p.end = p.stopAt
	m := tk.marks(p, p.end)
	if len(m) != 4 {
		t.Fatalf("%d marks, want 4 (one filled in)", len(m))
	}
	if d := m[2].Sub(at(60)).Seconds(); math.Abs(d) > 1 {
		t.Errorf("filled-in mark at %.1f s, want about 60", m[2].Sub(t0).Seconds())
	}
}

// Frames at 25 fps except where the turret faces 90-120°: those bins show
// the drop, and the router readings land in their bins.
func TestBinPass(t *testing.T) {
	t0 := time.Date(2026, 10, 21, 12, 0, 0, 0, time.UTC)
	at := func(s float64) time.Time { return t0.Add(time.Duration(s * float64(time.Second))) }
	const turn = 36.0 // 10° a second
	p := &sigPass{dir: 1, start: t0, end: at(turn)}
	marks := []time.Time{t0, at(turn)}
	var frames []time.Time
	for s := 0.0; s < turn; s += 0.04 {
		if s >= 9 && s < 12 && math.Mod(s, 0.5) > 0.04 {
			continue // about 2 fps between 90° and 120°
		}
		frames = append(frames, at(s))
	}
	router := []sigRouter{{at: at(4.5), dbm: -30, rxMbit: 72}, {at: at(10.5), dbm: -60, rxMbit: 6.5}}
	bins := binPass(p, p.end, angleFunc(1, marks, 0), frames, router)
	if b := bins[2]; b.Fps == nil || *b.Fps < 23 || *b.Fps > 26 {
		t.Errorf("20-30°: %+v", b.Fps)
	}
	if b := bins[10]; b.Fps == nil || *b.Fps > 4 || b.GapMs == nil || *b.GapMs < 400 {
		t.Errorf("100-110°: fps %v gap %v", *b.Fps, *b.GapMs)
	}
	if b := bins[4]; b.Dbm == nil || *b.Dbm != -30 {
		t.Errorf("40-50° router: %+v", b)
	}
	if b := bins[10]; b.Dbm == nil || *b.Dbm != -60 || *b.RxMbit != 6.5 {
		t.Errorf("100-110° router: %+v", b)
	}
	if b := bins[20]; b.Dbm != nil {
		t.Errorf("200-210° has no router reading but shows %v", *b.Dbm)
	}
}

// The real Jungle-06 run: its partner's hits in one beam came 1.7 s apart
// (a listening window missed between them). That's still one beam, so the
// pass carries on to its third turn instead of stopping after one.
func TestBeamWithAMissedWindowIsOneBeam(t *testing.T) {
	t0 := time.Date(2026, 10, 20, 21, 39, 42, 0, time.UTC) // in the past: beams are read up to now
	at := func(s float64) time.Time { return t0.Add(time.Duration(s * float64(time.Second))) }
	tk := &sigTank{hits: []time.Time{at(0.6), at(15.8), at(17.5), at(29.3), at(42.6)}}
	p := &sigPass{dir: 1, start: t0}
	tk.passes = []*sigPass{p}
	if bs := tk.turnBeams(p, at(50)); len(bs) != 3 {
		t.Fatalf("%d turn beams, want 3", len(bs))
	}
	first, ok := tk.finalBeamStart(p, 3)
	if !ok || !first.Equal(at(42.6)) {
		t.Fatalf("final beam %v %v, want at 42.6 s", ok, first.Sub(t0))
	}
}

// Jungle-06 turned at 42 s a turn one way and 30 s back: each pass's angles
// use its own pace, and the slow pass's 42 s gaps aren't taken as missed
// beams (1.4 of the faster pace).
func TestEachPassHasItsOwnPace(t *testing.T) {
	t0 := time.Date(2026, 10, 20, 21, 48, 20, 0, time.UTC)
	at := func(s float64) time.Time { return t0.Add(time.Duration(s * float64(time.Second))) }
	tk := &sigTank{hits: []time.Time{at(4.6), at(41.7), at(43.4), at(83.0), at(84.7), at(126.0),
		at(128.6), at(156.4), at(158.1), at(185.9), at(186.8), at(216.3)}}
	fwd := &sigPass{dir: 1, start: at(4), end: at(127.6), finalFirst: at(126.0), stopAt: at(127.6)}
	back := &sigPass{dir: -1, start: at(127.6), end: at(217.6), finalFirst: at(216.3), stopAt: at(217.6)}
	tk.passes = []*sigPass{fwd, back}
	end := at(220)
	if p := tk.passPeriod(fwd, end); math.Abs(p-42) > 1.5 {
		t.Errorf("first way: %.1f s a turn, want about 42", p)
	}
	if p := tk.passPeriod(back, end); math.Abs(p-30) > 1.5 {
		t.Errorf("back: %.1f s a turn, want about 30", p)
	}
	if m := tk.marks(fwd, end); len(m) != 2 {
		t.Errorf("first way: %d marks, want 2 (no made-up ones)", len(m))
	}
	if n, _ := tk.turnCount(fwd, end); n != 3 {
		t.Errorf("first way: %d turns counted, want 3", n)
	}
}

// A saved run read back charts the same as it did when it finished, and
// shows up in the list of runs.
func TestSavedRunLoadsTheSame(t *testing.T) {
	t0 := time.Date(2026, 10, 20, 21, 48, 20, 0, time.Local)
	// Whole milliseconds, as runs are saved.
	at := func(s float64) time.Time { return t0.Add(time.Duration(math.Round(s*1000)) * time.Millisecond) }
	tk := &sigTank{id: "A1", name: "Desert-01", num: 1, partner: "B6", hits: []time.Time{at(4.6), at(41.7), at(43.4), at(83.0), at(84.7), at(126.0),
		at(128.6), at(156.4), at(158.1), at(185.9), at(186.8), at(216.3)}}
	tk.passes = []*sigPass{
		{dir: 1, start: at(4), end: at(127.6), finalFirst: at(126.0), stopAt: at(127.6)},
		{dir: -1, start: at(127.6), end: at(217.6), finalFirst: at(216.3), stopAt: at(217.6)},
	}
	for s := 4.0; s < 217; s += 0.04 {
		if a := math.Mod((s-4)/42*360, 360); a > 90 && a < 120 && math.Mod(s, 0.5) > 0.04 {
			continue
		}
		tk.frames = append(tk.frames, at(s))
	}
	for s := 4.0; s < 217; s += 0.25 {
		tk.router = append(tk.router, sigRouter{at: at(s), dbm: -30 - int(s)%7, rxMbit: 72})
	}
	partner := &sigTank{id: "B6", name: "Jungle-06", num: 6, partner: "A1"}
	r := &sigRun{started: t0, ended: at(220), phase: "done", cfg: SignalTestSettings{Speed: 0.2, Turns: 3, Reverse: true, MaxTurnS: 90},
		order: []string{"A1", "B6"}, tanks: map[string]*sigTank{"A1": tk, "B6": partner}, routerOK: true}

	dir := t.TempDir()
	st := newSignalTest(nil, nil, "", dir)
	r.name = sigRunName(t0)
	st.mu.Lock()
	r.file = st.save(r)
	want := st.snapshotLocked(r)
	st.mu.Unlock()
	if r.file == "" {
		t.Fatal("not saved")
	}

	got, ok := st.savedSnapshot(r.file)
	if !ok {
		t.Fatal("can't load it back")
	}
	g, _ := json.Marshal(got.Tanks)
	w, _ := json.Marshal(want.Tanks)
	if string(g) != string(w) {
		t.Errorf("loaded run differs:\n got %s\nwant %s", g, w)
	}
	st.mu.Lock()
	runs := st.listRunsLocked()
	st.mu.Unlock()
	if len(runs) != 1 || runs[0].File != r.file || len(runs[0].Tanks) != 2 || runs[0].Tanks[0] != "Desert-01" {
		t.Errorf("runs: %+v", runs)
	}
	if _, ok := st.savedSnapshot("../settings.json"); ok {
		t.Error("loaded a file that isn't a run")
	}

	// A comment on it is kept (across a restart) and shown with it.
	if err := st.setComment(r.file, "  bigger antennas  "); err != nil {
		t.Fatal(err)
	}
	if st.setComment("../settings.json", "x") == nil {
		t.Error("commented on a file that isn't a run")
	}
	st2 := newSignalTest(nil, nil, "", dir)
	if v, _ := st2.savedSnapshot(r.file); v.Comment != "bigger antennas" {
		t.Errorf("comment after a restart: %q", v.Comment)
	}
	if v := st2.snapshot(); v.Run != r.file || v.Comment != "bigger antennas" {
		t.Errorf("last run after a restart: %q %q", v.Run, v.Comment)
	}
	st2.mu.Lock()
	runs = st2.listRunsLocked()
	st2.mu.Unlock()
	if runs[0].Comment != "bigger antennas" {
		t.Errorf("runs list comment: %q", runs[0].Comment)
	}
}

// Both directions are averaged bin by bin; a bin only one pass has keeps
// that pass's value.
func TestMergePasses(t *testing.T) {
	a := make([]sigBin, sigBins)
	b := make([]sigBin, sigBins)
	a[3].Fps, b[3].Fps = ptr(10), ptr(20)
	a[4].Fps = ptr(7)
	m := mergePasses([]sigPassView{{Dir: 1, Bins: a}, {Dir: -1, Bins: b}})
	if m[3].Fps == nil || *m[3].Fps != 15 || m[4].Fps == nil || *m[4].Fps != 7 || m[5].Fps != nil {
		t.Errorf("merged: %v %v %v", m[3].Fps, m[4].Fps, m[5].Fps)
	}
}

// Stopped part-way through driving forwards, every tank still drives back
// the same way, so it ends where it started.
func TestDrivingEndsWhereItStarted(t *testing.T) {
	ft := &fakeTanks{}
	st := newSignalTest(ft, nil, "", "")
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go st.driveLoop(ctx, []string{"A", "B"}, done)
	time.Sleep(sigDriveLeg / 2)
	cancel()
	<-done
	for _, id := range []string{"A", "B"} {
		var fwd, back []float64
		ft.mu.Lock()
		for _, m := range ft.sent {
			var c struct {
				Cmd string  `json:"cmd"`
				L   float64 `json:"l"`
				R   float64 `json:"r"`
			}
			if !strings.HasPrefix(m, id+" ") || json.Unmarshal([]byte(m[len(id)+1:]), &c) != nil || c.Cmd != "drive" {
				continue
			}
			if c.L != c.R {
				t.Errorf("%s: tracks differ: %s", id, m)
			}
			switch {
			case c.L > 0:
				fwd = append(fwd, c.L)
			case c.L < 0:
				back = append(back, -c.L)
			}
		}
		ft.mu.Unlock()
		if len(fwd) == 0 || fmt.Sprint(fwd) != fmt.Sprint(back) {
			t.Fatalf("%s: forwards %v, backwards %v", id, fwd, back)
		}
		if fwd[0] >= fwd[len(fwd)-1] {
			t.Errorf("%s: doesn't speed up: %v", id, fwd)
		}
	}
}
