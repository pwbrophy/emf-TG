package main

// Pretend infrared between fake tanks (-fake-tanks), so shooting, hits,
// death and respawn can be tried without hardware. While a fake tank's
// barrel is lit, every other fake tank that listens "sees" it, from a side
// that changes with each shot: front, right, rear, left.
//
// For the signal test (several barrels lit at once) fake turrets also turn:
// a listening tank's front sensor sees its partner's barrel while that
// turret points within fakeBeamDeg of straight at it.

import (
	"encoding/json"
	"math"
	"net/http"
	"strconv"
	"sync"
	"time"
)

type fakeWorld struct {
	mu      sync.Mutex
	emitter string // fake tank whose barrel is lit
	shots   int
	lit     map[string]bool        // every lit barrel
	turrets map[string]*fakeTurret // by tank id
}

// fakeTurret: angle 0 = facing forward; turns at fakeDegPerS × speed.
type fakeTurret struct {
	angle, speed float64
	at           time.Time
}

const (
	fakeDegPerS = 90 // 0.2 → 18°/s, 20 s a turn
	fakeBeamDeg = 12
)

func (t *fakeTurret) now(at time.Time, rate float64) float64 {
	t.angle = math.Mod(t.angle+at.Sub(t.at).Seconds()*t.speed*rate+3600, 360)
	t.at = at
	return t.angle
}

// fakeRate: each fake tank turns at its own slightly different speed.
func fakeRate(id string) float64 {
	n, _ := strconv.Atoi(id)
	return fakeDegPerS * (0.9 + 0.04*float64(n%6))
}

// N, E, S, W receivers (bits 0, 2, 4, 6).
var fakeHitMasks = []int{0x01, 0x04, 0x10, 0x40}

// fakeCommand plays a fake tank's part when the server sends it a command.
func (tm *tankManager) fakeCommand(id string, msg []byte) {
	var m struct {
		Cmd   string  `json:"cmd"`
		Ms    int     `json:"ms"`
		Speed float64 `json:"speed"`
	}
	if json.Unmarshal(msg, &m) != nil {
		return
	}
	f := &tm.fake
	f.mu.Lock()
	if f.lit == nil {
		f.lit, f.turrets = map[string]bool{}, map[string]*fakeTurret{}
	}
	f.mu.Unlock()
	switch m.Cmd {
	case "turret":
		f.mu.Lock()
		t := f.turrets[id]
		if t == nil {
			t = &fakeTurret{at: time.Now()}
			f.turrets[id] = t
		}
		t.now(time.Now(), fakeRate(id))
		t.speed = m.Speed
		f.mu.Unlock()
	case "ir_emit_left":
		f.mu.Lock()
		f.emitter = id
		f.lit[id] = true
		f.shots++
		f.mu.Unlock()
		go tm.fakeReport(id, tankReport{Cmd: "ir_emit_ack"}, 5*time.Millisecond)
	case "ir_emit_right":
		f.mu.Lock()
		f.emitter = id
		f.lit[id] = true
		f.mu.Unlock()
	case "ir_emit_stop":
		f.mu.Lock()
		if f.emitter == id {
			f.emitter = ""
		}
		delete(f.lit, id)
		f.mu.Unlock()
	case "ir_listen_window":
		partner := tm.fakePartner(id)
		f.mu.Lock()
		mask := 0
		if len(f.lit) == 1 && f.emitter != "" && f.emitter != id {
			mask = fakeHitMasks[(f.shots-1)%len(fakeHitMasks)] // a shot
		}
		if t := f.turrets[partner]; t != nil && f.lit[partner] {
			if a := t.now(time.Now(), fakeRate(partner)); a < fakeBeamDeg || a > 360-fakeBeamDeg {
				mask |= 0x01 // front sensor: the partner's barrel points at it
			}
		}
		f.mu.Unlock()
		go tm.fakeReport(id, tankReport{Cmd: "ir_window_result", Mask: &mask}, time.Duration(m.Ms)*time.Millisecond)
	case "ping":
		go tm.fakeReport(id, tankReport{Cmd: "pong"}, 3*time.Millisecond)
	}
}

// fakePartner: the fake tank opposite this one in the signal test (1↔6...).
func (tm *tankManager) fakePartner(id string) string {
	tm.mu.Lock()
	defer tm.mu.Unlock()
	s := tm.tanks[id]
	if s == nil {
		return ""
	}
	want := partnerNumber(tankNumber(s.label()))
	for pid, ps := range tm.tanks {
		if ps.fake != nil && tankNumber(ps.label()) == want {
			return pid
		}
	}
	return ""
}

func (tm *tankManager) fakeReport(id string, r tankReport, after time.Duration) {
	time.Sleep(after)
	if tm.onReport != nil {
		tm.onReport(id, r)
	}
}

// handleFakeRfid pretends a tank scanned a tag, to try respawning and base
// heals: POST /api/dev/rfid?tank=<id>&uid=<UID>. Only with -fake-tanks.
func (tm *tankManager) handleFakeRfid(w http.ResponseWriter, r *http.Request) {
	tank, uid := r.URL.Query().Get("tank"), r.URL.Query().Get("uid")
	if tank == "" || uid == "" {
		http.Error(w, "need tank and uid", http.StatusBadRequest)
		return
	}
	if tm.onReport != nil {
		tm.onReport(tank, tankReport{Cmd: "rfid", UID: uid})
	}
	w.WriteHeader(http.StatusNoContent)
}
