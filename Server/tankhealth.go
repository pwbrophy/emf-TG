package main

// Each tank's health over a day of games, for the event log (sessionlog.go).
//
// A tank's heartbeat (every 2 s) carries its free memory, its largest free
// block of memory, its Wi-Fi signal, its chip temperature and how long it has
// been running. One heartbeat says little; the trend over hours says whether a
// tank is slowly running out of memory or breaking it into small pieces (free
// memory steady but the largest block shrinking), which ends in a crash or a
// camera that won't restart, many games in. So every 30 s the hub writes one
// "tank" event per connected tank with the lowest figures since the last one.
//
// From the November 2026 firmware the heartbeat also has the longest time the
// tank's main loop was held up (lmax: the loop drives the motors' speed
// control and times the infrared, so a long hold-up is a jerk or a missed
// shot), the least free memory since it started (hmin), and how many times it
// has connected to the hub (wsn) and lost Wi-Fi (wfn) since it started, and
// how many times a barrel put itself out because its stop command never came
// (irto; it should stay 0).

import (
	"context"
	"sync"
	"time"
)

type tankHealth struct {
	mu       sync.Mutex
	beats    int
	lastAt   time.Time
	gapMax   time.Duration // longest wait between two heartbeats (2 s when all is well)
	heap     int64         // latest, bytes
	heapLow  int64         // lowest since the last take
	blk      int64         // largest free block, latest
	blkLow   int64
	rssiLow  int
	hasRSSI  bool
	temp     *float64
	upMs     int64
	loopMax  int // longest main-loop hold-up since the last take, ms; -1 = not reported
	heapEver *int
	wsN      *int
	wifiN    *int
	irStuck  *int

	// The hub's own pings (every 5 s): how long a command and its answer take.
	pings, pingLost int
	pingSum         time.Duration
	pingMax         time.Duration
}

// pong records the round trip of one of the hub's pings.
func (h *tankHealth) pong(rtt time.Duration) {
	h.mu.Lock()
	h.pings++
	h.pingSum += rtt
	h.pingMax = max(h.pingMax, rtt)
	h.mu.Unlock()
}

// lostPing records a ping that had no answer by the time the next was due.
func (h *tankHealth) lostPing() {
	h.mu.Lock()
	h.pingLost++
	h.mu.Unlock()
}

func (h *tankHealth) heartbeat(now time.Time, heap, blk, upMs int64, rssi *int, temp *float64, heapEver, loopMax, wsN, wifiN, irStuck *int) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if !h.lastAt.IsZero() {
		h.gapMax = max(h.gapMax, now.Sub(h.lastAt))
	}
	h.lastAt = now
	if h.beats == 0 {
		h.heapLow, h.blkLow, h.hasRSSI, h.loopMax = heap, blk, false, -1
	}
	h.beats++
	h.heap, h.blk, h.upMs, h.temp = heap, blk, upMs, temp
	h.heapLow, h.blkLow = min(h.heapLow, heap), min(h.blkLow, blk)
	if rssi != nil {
		if !h.hasRSSI || *rssi < h.rssiLow {
			h.rssiLow = *rssi
		}
		h.hasRSSI = true
	}
	if loopMax != nil {
		h.loopMax = max(h.loopMax, *loopMax)
	}
	h.heapEver, h.wsN, h.wifiN, h.irStuck = heapEver, wsN, wifiN, irStuck
}

// take returns the figures since the last take as event fields (nil if no
// heartbeat arrived) and starts a new period.
func (h *tankHealth) take() map[string]any {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.beats == 0 && h.pings == 0 && h.pingLost == 0 {
		return nil
	}
	defer func() { h.pings, h.pingLost, h.pingSum, h.pingMax = 0, 0, 0, 0 }()
	ping := func(f map[string]any) {
		if h.pings > 0 {
			f["ping_ms"] = (h.pingSum / time.Duration(h.pings)).Milliseconds()
			f["ping_max_ms"] = h.pingMax.Milliseconds()
		}
		if h.pingLost > 0 {
			f["pings_lost"] = h.pingLost
		}
	}
	if h.beats == 0 {
		f := map[string]any{}
		ping(f)
		return f
	}
	f := map[string]any{
		"heap_kb": h.heap / 1024, "heap_low_kb": h.heapLow / 1024,
		"blk_kb": h.blk / 1024, "blk_low_kb": h.blkLow / 1024,
		"up_s": h.upMs / 1000, "hb": h.beats, "hb_gap_max_ms": h.gapMax.Milliseconds(),
	}
	if h.hasRSSI {
		f["rssi_low"] = h.rssiLow
	}
	if h.temp != nil {
		f["temp_c"] = *h.temp
	}
	if h.loopMax >= 0 {
		f["loop_max_ms"] = h.loopMax
	}
	if h.heapEver != nil {
		f["heap_ever_low_kb"] = *h.heapEver / 1024
	}
	if h.wsN != nil {
		f["hub_connects"] = *h.wsN
	}
	if h.wifiN != nil {
		f["wifi_drops"] = *h.wifiN
	}
	if h.irStuck != nil && *h.irStuck > 0 {
		f["ir_barrel_left_on"] = *h.irStuck // should never happen: a barrel's stop command went missing
	}
	ping(f)
	h.beats, h.gapMax = 0, 0
	return f
}

// tankPingEvery is how often the hub pings each tank to time the command
// path (hub → tank → hub): what makes driving feel laggy, and what no other
// figure shows. A ping is a few bytes and the tank answers from its main loop.
const tankPingEvery = 5 * time.Second

// runHealth pings each tank every 5 s and writes each connected tank's
// "tank" event every 30 s.
func (tm *tankManager) runHealth(ctx context.Context) {
	if tm.events == nil {
		return
	}
	t := time.NewTicker(healthEvery)
	defer t.Stop()
	p := time.NewTicker(tankPingEvery)
	defer p.Stop()
	for {
		pinging := false
		select {
		case <-ctx.Done():
			return
		case <-p.C:
			pinging = true
		case <-t.C:
		}
		tm.mu.Lock()
		sessions := make([]*tankSession, 0, len(tm.tanks))
		for _, s := range tm.tanks {
			sessions = append(sessions, s)
		}
		tm.mu.Unlock()
		if pinging {
			quiet := tm.noPings != nil && tm.noPings()
			for _, s := range sessions {
				if s.conn == nil {
					continue // a fake tank
				}
				if s.pingAt.Swap(0) != 0 {
					s.health.lostPing() // the last one was never answered
				}
				if !quiet {
					s.pingAt.Store(time.Now().UnixNano())
					s.send(`{"cmd":"ping"}`)
				}
			}
			continue
		}
		for _, s := range sessions {
			f := s.health.take()
			dropped := s.dropped.Swap(0)
			if f == nil {
				if s.conn == nil || dropped == 0 {
					continue // a fake tank, or nothing to say
				}
				f = map[string]any{}
			}
			f["tank"] = s.label()
			if dropped > 0 {
				f["cmds_dropped"] = dropped
			}
			tm.events.add("tank", f)
		}
	}
}
