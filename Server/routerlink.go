package main

// The router's view of each tank, for the video log: its signal and the rates
// the router hears it at and sends to it, read from the Wi-Fi driver (iwinfo;
// only on the router) while any tank is streaming. A tank's own heartbeat
// can't say this. It shows whether a tank ever drops to a slow rate during a
// game, which takes airtime from the other tanks, and whether that is an
// 802.11b rate (1, 2, 5.5 or 11 Mbit/s). Each reading is a snapshot, so a very
// short drop between readings is missed.

import (
	"context"
	"errors"
	"log"
	"os/exec"
	"sync"
	"time"
)

const routerLinkEvery = 500 * time.Millisecond

// routerLinkStats is one stats sample's readings for one tank.
type routerLinkStats struct {
	DBm       float64 `json:"dbm"`       // signal the router hears, average
	RxMbitAvg float64 `json:"rxMbitAvg"` // rate the router hears the tank at
	RxMbitMin float64 `json:"rxMbitMin"`
	Rx11bPct  float64 `json:"rx11bPct"`  // share of readings at an 802.11b rate
	TxMbitMin float64 `json:"txMbitMin"` // slowest rate the router sent to the tank at
}

type routerWindow struct {
	n, rxN, txN, rx11b int
	dbmSum, rxSum      float64
	rxMin, txMin       float64
}

type routerLink struct {
	iface string
	rl    *relay
	mu    sync.Mutex
	win   map[string]*routerWindow // by MAC address
}

// startRouterLink returns nil (no readings) if iface is empty; every method
// is safe on a nil *routerLink.
func startRouterLink(iface string, rl *relay) *routerLink {
	if iface == "" {
		return nil
	}
	l := &routerLink{iface: iface, rl: rl, win: map[string]*routerWindow{}}
	go l.loop()
	return l
}

func (l *routerLink) loop() {
	warned := false
	for range time.Tick(routerLinkEvery) {
		if !l.streaming() {
			continue
		}
		ctx, cancel := context.WithTimeout(context.Background(), time.Second)
		out, err := exec.CommandContext(ctx, "iwinfo", l.iface, "assoclist").Output()
		cancel()
		if errors.Is(err, exec.ErrNotFound) {
			log.Printf("[VideoLog] no router readings: the hub isn't running on the router")
			return
		}
		if err != nil {
			if !warned {
				log.Printf("[VideoLog] no router readings for now: %v", err)
				warned = true
			}
			continue
		}
		warned = false
		l.add(parseAssocList(string(out)))
	}
}

func (l *routerLink) streaming() bool {
	for _, s := range l.rl.all() {
		if s.sources.Load() > 0 {
			return true
		}
	}
	return false
}

func (l *routerLink) add(samples map[string]sigRouter) {
	l.mu.Lock()
	defer l.mu.Unlock()
	for mac, s := range samples {
		w := l.win[mac]
		if w == nil {
			w = &routerWindow{}
			l.win[mac] = w
		}
		w.n++
		w.dbmSum += float64(s.dbm)
		if s.rxMbit > 0 {
			if w.rxN == 0 || s.rxMbit < w.rxMin {
				w.rxMin = s.rxMbit
			}
			w.rxN++
			w.rxSum += s.rxMbit
		}
		if s.rx11b {
			w.rx11b++
		}
		if s.txMbit > 0 {
			if w.txN == 0 || s.txMbit < w.txMin {
				w.txMin = s.txMbit
			}
			w.txN++
		}
	}
}

// take returns the readings since the last take, by MAC address.
func (l *routerLink) take() map[string]*routerLinkStats {
	if l == nil {
		return nil
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	out := make(map[string]*routerLinkStats, len(l.win))
	for mac, w := range l.win {
		st := &routerLinkStats{DBm: w.dbmSum / float64(w.n), RxMbitMin: w.rxMin, TxMbitMin: w.txMin}
		if w.rxN > 0 {
			st.RxMbitAvg = w.rxSum / float64(w.rxN)
			st.Rx11bPct = float64(w.rx11b) / float64(w.rxN) * 100
		}
		out[mac] = st
	}
	l.win = map[string]*routerWindow{}
	return out
}
