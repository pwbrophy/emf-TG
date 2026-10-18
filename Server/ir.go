package main

// The infrared shot handshake, a port of the Unity version's IrSlotScheduler.
// One shot runs at a time; each step waits for the tanks' replies, so timing
// adapts to Wi-Fi latency without any clock sync:
//
//  1. ir_emit_left to the shooter, wait for its ir_emit_ack.
//  2. ir_listen_window to every enemy; collect their ir_window_result masks
//     (which of their 8 receivers saw IR): b1.
//  3. If no enemy saw anything: miss.
//  4. ir_emit_right to the shooter and ir_listen_window to the enemies that
//     saw the left barrel, together: b2.
//  5. ir_emit_stop.
//  6. An enemy is hit only if it saw both barrels (b1 and b2 non-zero), which
//     rules out stray reflections. The side it was hit on comes from b1|b2.
//
// Dead enemies still listen, so the shooter can be told "That tank is
// already dead!" (their camera can't show the tank's lights).

import (
	"fmt"
	"log"
	"strings"
	"time"
)

// A shot that waited this long behind others is dropped rather than resolved
// seconds after the trigger was pulled.
const maxShotAge = 2 * time.Second

type shotRequest struct {
	shooter string // tank id
	at      time.Time
	gen     int // match it belongs to
}

// irReply is a tank's answer during a shot.
type irReply struct {
	tank string
	ack  bool // ir_emit_ack
	mask int  // ir_window_result mask, -1 if not a window result
}

type irScheduler struct {
	g     *game
	shots chan shotRequest
	inbox chan irReply
}

func newIrScheduler(g *game) *irScheduler {
	return &irScheduler{g: g, shots: make(chan shotRequest, 32), inbox: make(chan irReply, 128)}
}

func (s *irScheduler) run(stop <-chan struct{}) {
	for {
		select {
		case <-stop:
			return
		case r := <-s.shots:
			s.execute(r)
		}
	}
}

// enqueue queues a shot without blocking the caller (who holds game.mu).
func (s *irScheduler) enqueue(r shotRequest) bool {
	select {
	case s.shots <- r:
		return true
	default:
		return false
	}
}

// reply passes a tank's IR message to the shot in progress (never blocks).
func (s *irScheduler) reply(r irReply) {
	select {
	case s.inbox <- r:
	default:
	}
}

func (s *irScheduler) drain() {
	for {
		select {
		case <-s.inbox:
		default:
			return
		}
	}
}

func (s *irScheduler) execute(r shotRequest) {
	g := s.g
	if age := time.Since(r.at); age > maxShotAge {
		log.Printf("[IR] dropped a stale shot from %s (queued %.1fs ago)", r.shooter, age.Seconds())
		s.fireResult(r, "Miss (lag)")
		return
	}

	// Who's shooting at whom, read under the lock.
	g.mu.Lock()
	m := g.match
	if m == nil || m.gen != r.gen || g.phase != phasePlaying {
		g.mu.Unlock()
		return
	}
	shooter := m.fighters[r.shooter]
	if shooter == nil || shooter.alliance < 0 {
		g.mu.Unlock()
		return
	}
	var enemies []string
	for id, c := range m.fighters {
		if c.alliance >= 0 && c.alliance != shooter.alliance {
			enemies = append(enemies, id)
		}
	}
	st := g.settings
	g.mu.Unlock()
	if len(enemies) == 0 {
		return // nobody to hit (Unity aborts silently too)
	}

	window := time.Duration(st.IrWindowMs) * time.Millisecond
	ackTimeout := time.Duration(st.IrAckTimeoutMs) * time.Millisecond
	windowWait := window + time.Duration(st.IrWindowTimeoutMs)*time.Millisecond
	listen := fmt.Sprintf(`{"cmd":"ir_listen_window","ms":%d}`, st.IrWindowMs)
	send := g.tanks.sendTo

	s.drain()

	// 1. Left barrel.
	send(r.shooter, `{"cmd":"ir_emit_left"}`)
	if !s.waitAck(r.shooter, ackTimeout) {
		log.Printf("[IR] %s: no ack for the left barrel", shooter.tankName)
		send(r.shooter, `{"cmd":"ir_emit_stop"}`)
		s.fireResult(r, "Miss")
		return
	}

	// 2. Everyone listens.
	for _, id := range enemies {
		send(id, listen)
	}
	b1 := s.collect(enemies, windowWait)

	// 3. Early miss.
	var seen []string
	for _, id := range enemies {
		if b1[id] != 0 {
			seen = append(seen, id)
		}
	}
	if len(seen) == 0 {
		send(r.shooter, `{"cmd":"ir_emit_stop"}`)
		s.fireResult(r, "Miss")
		return
	}

	// 4. Right barrel, and listen again (no ack wait).
	s.drain()
	send(r.shooter, `{"cmd":"ir_emit_right"}`)
	for _, id := range seen {
		send(id, listen)
	}
	b2 := s.collect(seen, windowWait)

	// 5.
	send(r.shooter, `{"cmd":"ir_emit_stop"}`)

	// 6. Resolve.
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.match != m || g.phase != phasePlaying {
		return // the game ended while this shot was in the air
	}
	var parts []string
	hitDead := false
	for _, id := range enemies {
		if b1[id] == 0 || b2[id] == 0 {
			continue
		}
		target := m.fighters[id]
		mask := byte(b1[id] | b2[id])
		dir := resolveCardinal(mask)
		log.Printf("[IR] HIT %s -> %s, side %s (mask %08b)", shooter.tankName, target.tankName, dir, mask)
		if target.life != alive {
			hitDead = true
			continue
		}
		if target.invulnerable(time.Now()) {
			continue
		}
		dmg, killed := g.applyHit(shooter, target, mask, dir)
		if dmg > 0 && !killed {
			if dir == "S" {
				send(id, `{"cmd":"flash_hit","rear":true}`)
			} else {
				send(id, `{"cmd":"flash_hit"}`)
			}
		}
		g.sendSetHp(target)
		switch {
		case killed:
			parts = append(parts, "You killed "+target.crewNames()+"!")
		case dir == "S":
			parts = append(parts, "Hit "+target.crewNames()+" on rear")
		case dir == "N":
			parts = append(parts, "Hit "+target.crewNames()+" on front")
		default:
			parts = append(parts, "Hit "+target.crewNames()+" on flank")
		}
		if g.phase != phasePlaying {
			break // that kill won the game
		}
	}
	text := "Miss"
	if len(parts) > 0 {
		text = strings.Join(parts, " · ")
	} else if hitDead {
		text = "That tank is already dead!"
	}
	if c := m.fighters[r.shooter]; c != nil {
		g.sendTo(c.shooter().device, map[string]any{"t": "fire_result", "text": text})
	}
	g.broadcast()
}

func (s *irScheduler) waitAck(tank string, timeout time.Duration) bool {
	deadline := time.NewTimer(timeout)
	defer deadline.Stop()
	for {
		select {
		case <-deadline.C:
			return false
		case rep := <-s.inbox:
			if rep.ack && rep.tank == tank {
				return true
			}
		}
	}
}

// collect gathers window results from the given tanks until all have
// answered or the time is up. Tanks that don't answer count as "saw nothing".
func (s *irScheduler) collect(tanks []string, timeout time.Duration) map[string]int {
	want := map[string]bool{}
	for _, id := range tanks {
		want[id] = true
	}
	got := map[string]int{}
	deadline := time.NewTimer(timeout)
	defer deadline.Stop()
	for len(got) < len(want) {
		select {
		case <-deadline.C:
			return got
		case rep := <-s.inbox:
			if rep.mask >= 0 && want[rep.tank] {
				got[rep.tank] = rep.mask & 0xFF
			}
		}
	}
	return got
}

func (s *irScheduler) fireResult(r shotRequest, text string) {
	g := s.g
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.match == nil || g.match.gen != r.gen {
		return
	}
	if c := g.match.fighters[r.shooter]; c != nil {
		g.sendTo(c.shooter().device, map[string]any{"t": "fire_result", "text": text})
	}
}
