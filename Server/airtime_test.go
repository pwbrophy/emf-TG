package main

import (
	"errors"
	"os/exec"
	"testing"
	"time"
)

func newTestAirtime(on bool, err error) (*airtimeControl, chan string, chan time.Time) {
	calls := make(chan string, 8)
	a := &airtimeControl{iface: "ra0", wake: make(chan struct{}, 1), run: func(iface, value string) error {
		calls <- iface + "=" + value
		return err
	}}
	a.want.Store(on)
	tick := make(chan time.Time)
	go a.loop(tick)
	return a, calls, tick
}

func wantCall(t *testing.T, calls chan string, want string) {
	t.Helper()
	select {
	case got := <-calls:
		if got != want {
			t.Errorf("set %s, want %s", got, want)
		}
	case <-time.After(2 * time.Second):
		t.Fatalf("no call, want %s", want)
	}
}

// Set at start, when the host changes it, and again on every tick (the
// router turns it back on when its Wi-Fi restarts).
func TestAirtimeFairnessIsKeptSet(t *testing.T) {
	a, calls, tick := newTestAirtime(false, nil)
	wantCall(t, calls, "ra0=0")
	tick <- time.Now()
	wantCall(t, calls, "ra0=0")
	a.set(false) // no change: nothing to do
	select {
	case got := <-calls:
		t.Errorf("unexpected %s", got)
	case <-time.After(50 * time.Millisecond):
	}
	a.set(true)
	wantCall(t, calls, "ra0=1")
	tick <- time.Now()
	wantCall(t, calls, "ra0=1")
}

// A failure keeps trying (the interface may be restarting); no iwpriv at all,
// as on a PC, stops.
func TestAirtimeFairnessFailures(t *testing.T) {
	_, calls, tick := newTestAirtime(false, errors.New("interface down"))
	wantCall(t, calls, "ra0=0")
	tick <- time.Now()
	wantCall(t, calls, "ra0=0")

	_, calls, tick = newTestAirtime(false, &exec.Error{Name: "iwpriv", Err: exec.ErrNotFound})
	wantCall(t, calls, "ra0=0")
	select {
	case tick <- time.Now():
		t.Error("still running without iwpriv")
	case <-time.After(50 * time.Millisecond):
	}
}

// Changing the setting reaches the router; nil (no Wi-Fi to control) is safe.
func TestAirtimeFairnessSetting(t *testing.T) {
	g, _ := newTestGame(t)
	if g.settings.AirtimeFairness {
		t.Error("should default to off")
	}
	s := g.settings
	s.AirtimeFairness = true
	g.mu.Lock()
	g.applySettings(s) // g.airtime is nil here
	if !g.settings.AirtimeFairness {
		t.Error("setting not kept")
	}
	a, calls, _ := newTestAirtime(true, nil)
	wantCall(t, calls, "ra0=1")
	g.airtime = a
	s.AirtimeFairness = false
	g.applySettings(s)
	g.mu.Unlock()
	wantCall(t, calls, "ra0=0")
}
