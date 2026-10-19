package main

import (
	"math"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"
)

// playingTank: Alice is playing on tank "a".
func playingTank(t *testing.T) (*game, *fakeTanks, *testPhone, *testPhone) {
	t.Helper()
	g, ft := newTestGame(t, "Desert-01")
	h := hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	a.send(inMsg{T: "pick_tank", Tank: "a"})
	h.send(inMsg{T: "host_start"})
	g.mu.Lock()
	g.phaseEndsAt = time.Now()
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	if g.phase != phasePlaying {
		t.Fatalf("phase %s", g.phase)
	}
	return g, ft, h, a
}

// lastTurret is the last turret speed sent to a tank, and how many were sent.
func lastTurret(ft *fakeTanks, id string) (float64, int) {
	ft.mu.Lock()
	defer ft.mu.Unlock()
	prefix := id + ` {"cmd":"turret","speed":`
	v, n := 0.0, 0
	for _, s := range ft.sent {
		if strings.HasPrefix(s, prefix) {
			v, _ = strconv.ParseFloat(strings.TrimSuffix(strings.TrimPrefix(s, prefix), "}"), 64)
			n++
		}
	}
	return v, n
}

// run ticks the stabiliser for d of game time from start.
func run(g *game, start time.Time, d time.Duration) time.Time {
	for t := time.Duration(0); t < d; t += stabTickEvery {
		start = start.Add(stabTickEvery)
		g.tickStabiliser(start, stabTickEvery)
	}
	return start
}

func near(a, b float64) bool { return math.Abs(a-b) < 0.011 }

func TestStabiliserCountersHullTurnAndAddsPlayerTurret(t *testing.T) {
	g, ft, _, a := playingTank(t)
	g.settings.Tuning.StabSpinUpMs, g.settings.Tuning.StabSpinDownMs = 0, 0 // no inertia: exact numbers

	// Off: turning the hull leaves the turret alone.
	now := time.Now()
	a.send(inMsg{T: "drive", L: 0.6, R: -0.6})
	now = run(g, now, 100*time.Millisecond)
	if _, n := lastTurret(ft, "a"); n != 0 {
		t.Fatal("the turret moved with stabilisation off")
	}

	a.send(inMsg{T: "stabilise", On: true})
	if !a.state().Me.Stabilise {
		t.Fatal("the page isn't told stabilisation is on")
	}
	a.send(inMsg{T: "drive", L: 0.6, R: -0.6}) // spinning right
	now = run(g, now, 100*time.Millisecond)
	// Gain 0.5 × turn 0.6 = 0.3, turning left (positive) against the hull.
	if v, _ := lastTurret(ft, "a"); !near(v, 0.3) {
		t.Fatalf("turret %.3f while spinning right, want 0.300", v)
	}

	// The player's slider adds on top, and is sent at once.
	a.send(inMsg{T: "turret", S: -0.2})
	if v, _ := lastTurret(ft, "a"); !near(v, 0.1) {
		t.Fatalf("turret %.3f with the slider at -0.2, want 0.100", v)
	}

	// Hull stops: only the slider is left.
	a.send(inMsg{T: "drive", L: 0, R: 0})
	now = run(g, now, 100*time.Millisecond)
	if v, _ := lastTurret(ft, "a"); !near(v, -0.2) {
		t.Fatalf("turret %.3f after the hull stopped, want -0.200", v)
	}

	// Spinning left counters the other way, capped at StabMax.
	a.send(inMsg{T: "turret", S: 0})
	a.send(inMsg{T: "drive", L: -1, R: 1})
	run(g, now, 100*time.Millisecond)
	if v, _ := lastTurret(ft, "a"); !near(v, -g.settings.Tuning.StabMax) {
		t.Fatalf("turret %.3f spinning left at full, want -StabMax", v)
	}

	// Off again: the turret goes back to the slider at once.
	a.send(inMsg{T: "turret", S: 0.2})
	a.send(inMsg{T: "stabilise", On: false})
	if v, _ := lastTurret(ft, "a"); !near(v, 0.2) {
		t.Fatalf("turret %.3f after switching off, want the slider's 0.200", v)
	}
}

func TestStabiliserFollowsHullInertiaAndWatchdog(t *testing.T) {
	g, ft, _, a := playingTank(t)
	a.send(inMsg{T: "stabilise", On: true})
	a.send(inMsg{T: "drive", L: 0.6, R: -0.6})

	// Spin-up takes StabSpinUpMs (150 ms): partway after 60 ms, nearly there after 600.
	now := run(g, time.Now(), 60*time.Millisecond)
	if v, _ := lastTurret(ft, "a"); v <= 0.02 || v >= 0.25 {
		t.Fatalf("turret %.3f 60 ms into a turn: should be ramping up", v)
	}
	g.mu.Lock()
	g.match.fighters["a"].stab.driveAt = now // keep the drive command fresh
	g.mu.Unlock()
	now = run(g, now, 400*time.Millisecond)
	if v, _ := lastTurret(ft, "a"); v < 0.27 || v > 0.3 {
		t.Fatalf("turret %.3f after 460 ms of turning, want about 0.3", v)
	}

	// No drive command for longer than the firmware's watchdog: the tank has
	// stopped, so the correction dies away to exactly 0.
	run(g, now, driveWatchdog+time.Second)
	if v, _ := lastTurret(ft, "a"); v != 0 {
		t.Fatalf("turret %.3f after the drive watchdog, want 0", v)
	}
}

func TestStabiliserRampsLikeTheFirmware(t *testing.T) {
	if v := rampToward(0, 1, 0.5, 0, 100*time.Millisecond); !near(v, 0.2) {
		t.Fatalf("0.5 s accel after 100 ms: %.3f, want 0.2", v)
	}
	if v := rampToward(0.5, 0, 0.5, 0, time.Millisecond); v != 0 {
		t.Fatalf("decel 0 should stop at once: %.3f", v)
	}
	tu := defaultTuning()
	if v := tu.stabTurnRate(0.5, 0.5); v != 0 {
		t.Fatalf("driving straight turns the turret: %.3f", v)
	}
	if v := tu.stabTurnRate(0.53, 0.47); v != 0 {
		t.Fatalf("a turn inside the dead band turns the turret: %.3f", v)
	}
	tu.StabArcGain = 2
	if spin, arc := tu.stabTurnRate(0.3, -0.3), tu.stabTurnRate(1, 0.4); arc <= spin {
		t.Fatalf("arc gain 2 should correct a driving turn more than a spin: %.3f vs %.3f", arc, spin)
	}
}

func TestStabiliserPausedAndNotAllowed(t *testing.T) {
	g, ft, h, a := playingTank(t)
	a.send(inMsg{T: "stabilise", On: true})
	a.send(inMsg{T: "drive", L: 0.6, R: -0.6})
	now := run(g, time.Now(), 40*time.Millisecond)
	h.send(inMsg{T: "host_pause", On: true})
	_, before := lastTurret(ft, "a")
	now = run(g, now, 200*time.Millisecond)
	if _, n := lastTurret(ft, "a"); n != before {
		t.Fatal("the stabiliser drove the turret while paused")
	}
	h.send(inMsg{T: "host_pause", On: false})

	// The host switches the feature off: the turret is handed back to the slider.
	a.send(inMsg{T: "drive", L: 0.6, R: -0.6})
	run(g, now, 200*time.Millisecond)
	s := g.settings
	s.Tuning.StabAllowed = false
	h.send(inMsg{T: "host_settings", Settings: &s})
	run(g, now.Add(time.Second), 40*time.Millisecond)
	if v, _ := lastTurret(ft, "a"); v != 0 {
		t.Fatalf("turret %.3f after the host switched stabilisation off", v)
	}
	if st := a.state(); st.Controls.StabAllowed {
		t.Fatal("the page still shows the STAB button")
	}
}

// Old presets don't have the stabiliser settings: they load with the defaults.
func TestOldPresetKeepsStabiliserDefaults(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01")
	g.writePresets(map[string]Tuning{})
	g.presets = nil
	if err := os.WriteFile(g.presetsPath, []byte(`{"Old":{"driveMin":0.7}}`), 0o600); err != nil {
		t.Fatal(err)
	}
	p := g.readPresets()["Old"]
	if p.DriveMin != 0.7 || !p.StabAllowed || p.StabGain != defaultTuning().StabGain {
		t.Fatalf("old preset loaded as %+v", p)
	}
}
