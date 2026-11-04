package main

// The router's airtime fairness (Settings.AirtimeFairness). With it on, tanks
// that vanish in quick succession leave undeliverable packets in the Wi-Fi
// driver, and every live tank slows down until the driver drops those tanks,
// about 8½ minutes later (3 November 2026: about 20 fps and 41 ms sends with it
// on, 25 fps and 13 ms with it off, the dead tanks still listed).
//
// It is one switch for both bands, and only a runtime command (iwpriv): the
// router's own files turn it back on when its Wi-Fi restarts. So the hub sets
// it at start, when the host changes it, and every minute after that.

import (
	"errors"
	"log"
	"os/exec"
	"sync/atomic"
	"time"
)

const airtimeEvery = time.Minute

type airtimeControl struct {
	iface string
	want  atomic.Bool
	wake  chan struct{}
	run   func(iface, value string) error
}

// startAirtimeControl returns nil (nothing to control) if iface is empty;
// set is safe on a nil *airtimeControl.
func startAirtimeControl(iface string, on bool) *airtimeControl {
	if iface == "" {
		return nil
	}
	a := &airtimeControl{iface: iface, wake: make(chan struct{}, 1), run: runIwprivAirtime}
	a.want.Store(on)
	go a.loop(time.NewTicker(airtimeEvery).C)
	return a
}

func runIwprivAirtime(iface, value string) error {
	return exec.Command("iwpriv", iface, "set", "vow_airtime_fairness_en="+value).Run()
}

func (a *airtimeControl) set(on bool) {
	if a == nil || a.want.Swap(on) == on {
		return
	}
	select {
	case a.wake <- struct{}{}:
	default:
	}
}

func (a *airtimeControl) loop(tick <-chan time.Time) {
	warned, last := false, ""
	for {
		value := "0"
		if a.want.Load() {
			value = "1"
		}
		err := a.run(a.iface, value)
		switch {
		case errors.Is(err, exec.ErrNotFound):
			log.Printf("[Wi-Fi] can't set airtime fairness: the hub isn't running on the router")
			return
		case err != nil:
			if !warned {
				log.Printf("[Wi-Fi] can't set airtime fairness yet: %v", err)
				warned = true
			}
		default:
			warned = false
			if value != last {
				log.Printf("[Wi-Fi] airtime fairness set to %s", value)
				last = value
			}
		}
		select {
		case <-a.wake:
		case <-tick:
		}
	}
}
