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
//
// Turn-based games (turns.go): shots listen for longer (Turns.ShotWindowMs),
// and tanks on overwatch send silent probes whenever no shot is waiting:
// the same handshake with a short window, where only moving enemies listen.
// A probe that hits becomes the overwatch tank's one shot.

import (
	"fmt"
	"log"
	"slices"
	"strings"
	"sync/atomic"
	"time"
)

// A shot that waited this long behind others is dropped rather than resolved
// seconds after the trigger was pulled.
const maxShotAge = 2 * time.Second

type shotRequest struct {
	shooter  string // tank id
	at       time.Time
	gen      int  // match it belongs to
	windowMs int  // how long enemies listen for each barrel; 0 = Settings.IrWindowMs
	probe    bool // an overwatch probe (turns.go)
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

	// probing is set while a turn-based game has tanks on overwatch: the
	// scheduler then asks for a probe whenever no shot is waiting. wake
	// rouses it when probing starts. In the realtime game neither is used:
	// the scheduler just waits for shots.
	probing atomic.Bool
	wake    chan struct{}
}

// probeIdle is how often the scheduler looks for an overwatch probe to send
// while no shot is waiting.
const probeIdle = 20 * time.Millisecond

func newIrScheduler(g *game) *irScheduler {
	return &irScheduler{g: g, shots: make(chan shotRequest, 32), inbox: make(chan irReply, 128), wake: make(chan struct{}, 1)}
}

// setProbing switches overwatch probing on or off (turns.go).
func (s *irScheduler) setProbing(on bool) {
	if s.probing.Swap(on) != on && on {
		select {
		case s.wake <- struct{}{}:
		default:
		}
	}
}

func (s *irScheduler) run(stop <-chan struct{}) {
	for {
		// Shots first: a probe only runs when none is waiting.
		select {
		case <-stop:
			return
		case r := <-s.shots:
			s.guarded(r)
			continue
		default:
		}
		if s.probing.Load() {
			if r, ok := s.g.nextProbe(); ok { // turns.go
				s.guarded(r)
				continue
			}
		}
		var idle <-chan time.Time // nil (never) unless probing
		if s.probing.Load() {
			idle = time.After(probeIdle)
		}
		select {
		case <-stop:
			return
		case r := <-s.shots:
			s.guarded(r)
		case <-idle:
		case <-s.wake:
		}
	}
}

// guarded runs one shot; a panic in it is logged instead of ending the hub.
func (s *irScheduler) guarded(r shotRequest) {
	guard("a shot", s.g.events, func() { s.execute(r) })
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

// shotRecord is what one shot leaves in the event log: how long each step
// took and what every tank saw, so a "Miss" can be told apart afterwards: the
// barrel wasn't seen (aim, range, a dead IR LED), only one barrel was seen, a
// tank answered late (Wi-Fi), or the shot waited too long behind others.
type shotRecord struct {
	result            string
	shooter, player   string
	queued, ack       time.Duration // waiting behind other shots; the shooter's "barrel lit"
	left, right       time.Duration // collecting what the enemies saw of each barrel
	listeners         int
	offline, silent   []string       // enemies not connected; enemies that didn't answer in time
	sawLeft, sawRight map[string]int // tank name → sensor mask (only tanks that saw something)
	hits              []map[string]any
}

// logShot writes the shot's event and adds it to the game's tally. Caller
// holds game.mu.
func (s *irScheduler) logShot(r shotRequest, m *match, rec shotRecord) {
	total := time.Since(r.at)
	if r.probe && rec.result != "hit" {
		return // overwatch probes run many times a second; only one that fires is news
	}
	m.shots.add(rec.result, len(rec.silent), int(total.Milliseconds()))
	if s.g.events == nil {
		return
	}
	f := map[string]any{
		"game": m.gen, "tank": rec.shooter, "player": rec.player, "result": rec.result,
		"queued_ms": rec.queued.Milliseconds(), "total_ms": total.Milliseconds(),
	}
	if rec.ack > 0 {
		f["ack_ms"] = rec.ack.Milliseconds()
	}
	if rec.listeners > 0 {
		f["listeners"], f["left_ms"] = rec.listeners, rec.left.Milliseconds()
	}
	if rec.right > 0 {
		f["right_ms"] = rec.right.Milliseconds()
	}
	if len(rec.offline) > 0 {
		f["offline"] = rec.offline
	}
	if len(rec.silent) > 0 {
		f["silent"] = rec.silent
	}
	if len(rec.sawLeft) > 0 {
		f["saw_left"] = rec.sawLeft
	}
	if len(rec.sawRight) > 0 {
		f["saw_right"] = rec.sawRight
	}
	if len(rec.hits) > 0 {
		f["hits"] = rec.hits
	}
	if r.probe {
		f["overwatch"] = true
	}
	s.g.events.add("shot", f)
}

func (s *irScheduler) execute(r shotRequest) {
	g := s.g
	rec := shotRecord{queued: time.Since(r.at)}

	// Who's shooting at whom, read under the lock.
	g.mu.Lock()
	m := g.match
	if m == nil || m.gen != r.gen || g.phase != phasePlaying {
		g.mu.Unlock()
		return
	}
	shooter := m.fighters[r.shooter]
	if shooter == nil || shooter.alliance < 0 || (r.probe && !shooter.turn.overwatch) {
		g.mu.Unlock()
		return
	}
	rec.shooter, rec.player = shooter.tankName, shooter.shooter().name
	names := map[string]string{} // tank id → name, for the log
	var enemies []string
	for id, c := range m.fighters {
		if c.alliance >= 0 && c.alliance != shooter.alliance && (!r.probe || g.movingTarget(c, time.Now())) {
			enemies = append(enemies, id)
			names[id] = c.tankName
		}
	}
	st := g.settings
	g.mu.Unlock()

	// early ends a shot before anything is resolved.
	early := func(result, text string) {
		rec.result = result
		g.mu.Lock()
		defer g.mu.Unlock()
		if g.match != m {
			return
		}
		if text != "" && !r.probe {
			g.sendTo(shooter.shooter().device, map[string]any{"t": "fire_result", "text": text})
		}
		s.logShot(r, m, rec)
	}

	if rec.queued > maxShotAge {
		log.Printf("[IR] dropped a stale shot from %s (queued %.1fs ago)", rec.shooter, rec.queued.Seconds())
		early("stale", "Miss (lag)")
		return
	}
	if len(enemies) == 0 {
		return // nobody to hit (Unity aborts silently too)
	}

	windowMs := st.IrWindowMs
	if r.windowMs > 0 {
		windowMs = r.windowMs
	}
	window := time.Duration(windowMs) * time.Millisecond
	ackTimeout := time.Duration(st.IrAckTimeoutMs) * time.Millisecond
	windowWait := window + time.Duration(st.IrWindowTimeoutMs)*time.Millisecond
	listen := fmt.Sprintf(`{"cmd":"ir_listen_window","ms":%d}`, windowMs)
	send := g.tanks.sendTo

	// listenAll asks tanks to listen and returns the ones that will answer: a
	// tank that isn't connected (flat battery, restarting) is left out, or
	// every shot at its alliance would wait the whole timeout for it and the
	// shots behind would back up into "Miss (lag)".
	listenAll := func(ids []string) (listening []string) {
		for _, id := range ids {
			if send(id, listen) {
				listening = append(listening, id)
			} else if !slices.Contains(rec.offline, names[id]) {
				rec.offline = append(rec.offline, names[id])
			}
		}
		return listening
	}
	noteSilent := func(asked []string, got map[string]int) {
		for _, id := range asked {
			if _, ok := got[id]; !ok && !slices.Contains(rec.silent, names[id]) {
				rec.silent = append(rec.silent, names[id])
			}
		}
	}

	s.drain()

	// 1. Left barrel.
	at := time.Now()
	if !send(r.shooter, `{"cmd":"ir_emit_left"}`) {
		early("shooter_offline", "Miss")
		return
	}
	if !s.waitAck(r.shooter, ackTimeout) {
		log.Printf("[IR] %s: no ack for the left barrel", shooter.tankName)
		send(r.shooter, `{"cmd":"ir_emit_stop"}`)
		rec.ack = time.Since(at)
		early("no_ack", "Miss")
		return
	}
	rec.ack = time.Since(at)

	// 2. Everyone listens.
	at = time.Now()
	listening := listenAll(enemies)
	rec.listeners = len(listening)
	b1 := s.collect(listening, windowWait)
	rec.left = time.Since(at)
	noteSilent(listening, b1)

	// 3. Early miss.
	var seen []string
	for _, id := range listening {
		if b1[id] != 0 {
			seen = append(seen, id)
			if rec.sawLeft == nil {
				rec.sawLeft = map[string]int{}
			}
			rec.sawLeft[names[id]] = b1[id]
		}
	}
	if len(seen) == 0 {
		send(r.shooter, `{"cmd":"ir_emit_stop"}`)
		early("miss", "Miss")
		return
	}

	// 4. Right barrel, and listen again (no ack wait).
	s.drain()
	at = time.Now()
	send(r.shooter, `{"cmd":"ir_emit_right"}`)
	again := listenAll(seen)
	b2 := s.collect(again, windowWait)
	rec.right = time.Since(at)
	noteSilent(again, b2)
	for _, id := range again {
		if b2[id] != 0 {
			if rec.sawRight == nil {
				rec.sawRight = map[string]int{}
			}
			rec.sawRight[names[id]] = b2[id]
		}
	}

	// 5.
	send(r.shooter, `{"cmd":"ir_emit_stop"}`)

	// 6. Resolve.
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.match != m || g.phase != phasePlaying {
		return // the game ended while this shot was in the air
	}
	if r.probe {
		// Only a moving enemy that saw both barrels counts; then the probe
		// becomes the overwatch tank's one shot.
		var hit []string
		now := time.Now()
		for _, id := range enemies {
			if b1[id] != 0 && b2[id] != 0 && m.fighters[id].life == alive && g.movingTarget(m.fighters[id], now) {
				hit = append(hit, id)
			}
		}
		if len(hit) == 0 || !g.overwatchFires(shooter) { // turns.go
			return
		}
		enemies = hit
	}
	var parts []string
	hitDead := false
	both := 0 // enemies that saw both barrels
	for _, id := range enemies {
		if b1[id] == 0 || b2[id] == 0 {
			continue
		}
		both++
		target := m.fighters[id]
		mask := byte(b1[id] | b2[id])
		dir := resolveCardinal(mask)
		log.Printf("[IR] HIT %s -> %s, side %s (mask %08b)", shooter.tankName, target.tankName, dir, mask)
		hit := map[string]any{"tank": target.tankName, "dir": dir, "mask": int(mask)}
		rec.hits = append(rec.hits, hit)
		if target.life != alive {
			hitDead = true
			hit["effect"] = "already " + target.life.String()
			continue
		}
		if target.invulnerable(time.Now()) {
			hit["effect"] = "invulnerable"
			continue
		}
		dmg, killed := g.applyHit(shooter, target, mask, dir)
		hit["dmg"], hit["killed"], hit["hp_left"] = dmg, killed, target.hp
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
	rec.result = "one_barrel"
	if len(parts) > 0 {
		text = strings.Join(parts, " · ")
		rec.result = "hit"
	} else if hitDead {
		text = "That tank is already dead!"
		rec.result = "no_effect"
	} else if both > 0 {
		rec.result = "no_effect" // couldn't be hurt just now
	}
	if r.probe {
		text = "Overwatch: " + text
	}
	if c := m.fighters[r.shooter]; c != nil {
		g.sendTo(c.shooter().device, map[string]any{"t": "fire_result", "text": text})
	}
	s.logShot(r, m, rec)
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
