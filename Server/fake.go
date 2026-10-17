package main

// Pretend infrared between fake tanks (-fake-tanks), so shooting, hits,
// death and respawn can be tried without hardware. While a fake tank's
// barrel is lit, every other fake tank that listens "sees" it, from a side
// that changes with each shot: front, right, rear, left.

import (
	"encoding/json"
	"net/http"
	"sync"
	"time"
)

type fakeWorld struct {
	mu      sync.Mutex
	emitter string // fake tank whose barrel is lit
	shots   int
}

// N, E, S, W receivers (bits 0, 2, 4, 6).
var fakeHitMasks = []int{0x01, 0x04, 0x10, 0x40}

// fakeCommand plays a fake tank's part when the server sends it a command.
func (tm *tankManager) fakeCommand(id string, msg []byte) {
	var m struct {
		Cmd string `json:"cmd"`
		Ms  int    `json:"ms"`
	}
	if json.Unmarshal(msg, &m) != nil {
		return
	}
	f := &tm.fake
	switch m.Cmd {
	case "ir_emit_left":
		f.mu.Lock()
		f.emitter = id
		f.shots++
		f.mu.Unlock()
		go tm.fakeReport(id, tankReport{Cmd: "ir_emit_ack"}, 5*time.Millisecond)
	case "ir_emit_right":
		f.mu.Lock()
		f.emitter = id
		f.mu.Unlock()
	case "ir_emit_stop":
		f.mu.Lock()
		if f.emitter == id {
			f.emitter = ""
		}
		f.mu.Unlock()
	case "ir_listen_window":
		f.mu.Lock()
		mask := 0
		if f.emitter != "" && f.emitter != id {
			mask = fakeHitMasks[(f.shots-1)%len(fakeHitMasks)]
		}
		f.mu.Unlock()
		go tm.fakeReport(id, tankReport{Cmd: "ir_window_result", Mask: &mask}, time.Duration(m.Ms)*time.Millisecond)
	case "ping":
		go tm.fakeReport(id, tankReport{Cmd: "pong"}, 3*time.Millisecond)
	}
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
