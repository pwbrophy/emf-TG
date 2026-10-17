package main

import (
	"encoding/json"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestResolveCardinal(t *testing.T) {
	cases := map[byte]string{
		0x01: "N", 0x04: "E", 0x10: "S", 0x40: "W",
		0x03: "N", // N + NE
		0x18: "S", // SE + S
		0x30: "S", // S + SW
		0x44: "N", // E + W cancel out
		0xFF: "N", // everything cancels out
		0x00: "N",
	}
	for mask, want := range cases {
		if got := resolveCardinal(mask); got != want {
			t.Errorf("mask %08b: got %s, want %s", mask, got, want)
		}
	}
}

// irWorld plays the tanks' side of the shot handshake from a script: the
// mask each tank reports for the left (b1) and right (b2) barrel.
type irWorld struct {
	mu     sync.Mutex
	b1, b2 map[string]int
	right  bool
	noAck  bool
}

func (w *irWorld) set(b1, b2 map[string]int) {
	w.mu.Lock()
	w.b1, w.b2 = b1, b2
	w.mu.Unlock()
}

// combatGame: Alice on Desert-01 (tank a) against Bob on Jungle-02 (tank b),
// both in a running game with the shot scheduler going.
func combatGame(t *testing.T, tune func(*Settings)) (*game, *fakeTanks, *irWorld, *testPhone, *testPhone, *testPhone) {
	t.Helper()
	g, ft := newTestGame(t, "Desert-01", "Jungle-02")
	g.settings.IrWindowMs = 5
	g.settings.IrAckTimeoutMs = 100
	g.settings.IrWindowTimeoutMs = 50
	g.settings.FireCooldownMs = 250
	if tune != nil {
		tune(&g.settings)
	}
	w := &irWorld{}
	ft.onSend = func(id, msg string) {
		w.mu.Lock()
		defer w.mu.Unlock()
		switch {
		case strings.Contains(msg, `"ir_emit_left"`):
			w.right = false
			if !w.noAck {
				go g.tankMessage(id, tankReport{Cmd: "ir_emit_ack"})
			}
		case strings.Contains(msg, `"ir_emit_right"`):
			w.right = true
		case strings.Contains(msg, `"ir_listen_window"`):
			mask := w.b1[id]
			if w.right {
				mask = w.b2[id]
			}
			go g.tankMessage(id, tankReport{Cmd: "ir_window_result", Mask: &mask})
		}
	}
	stop := make(chan struct{})
	go g.ir.run(stop)
	t.Cleanup(func() { close(stop) })

	h := hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	a.send(inMsg{T: "pick_tank", Tank: "a"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})
	b.send(inMsg{T: "pick_tank", Tank: "b"})
	h.send(inMsg{T: "host_start"})
	g.mu.Lock()
	g.phaseEndsAt = time.Now()
	g.mu.Unlock()
	g.tick()
	if g.phase != phasePlaying {
		t.Fatalf("phase %s, want playing", g.phase)
	}
	return g, ft, w, h, a, b
}

// waitFor reads p's messages until one of type typ arrives.
func (p *testPhone) waitFor(typ string) map[string]any {
	p.t.Helper()
	deadline := time.After(2 * time.Second)
	for {
		select {
		case b := <-p.c.out:
			var m map[string]any
			_ = json.Unmarshal(b, &m)
			if m["t"] == typ {
				return m
			}
		case <-deadline:
			p.t.Fatalf("no %q message arrived", typ)
			return nil
		}
	}
}

func (g *game) hpOf(tank string) int {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.match.fighters[tank].hp
}

func shoot(g *game, p *testPhone) string {
	p.t.Helper()
	time.Sleep(260 * time.Millisecond) // past the reload time from any earlier shot
	p.send(inMsg{T: "fire"})
	return p.waitFor("fire_result")["text"].(string)
}

func TestRearHitDoesTripleDamage(t *testing.T) {
	g, ft, w, _, a, b := combatGame(t, nil)
	w.set(map[string]int{"b": 0x10}, map[string]int{"b": 0x10}) // Bob sees both barrels from behind

	if text := shoot(g, a); text != "Hit Bob on rear" {
		t.Fatalf("Alice was told %q", text)
	}
	if hp := g.hpOf("b"); hp != 25 { // 100 - 25 × 3
		t.Fatalf("Bob's HP %d, want 25", hp)
	}
	hit := b.waitFor("hit_taken")
	if hit["shooter"] != "Alice" || hit["dir"] != "S" {
		t.Fatalf("Bob's hit message: %v", hit)
	}
	if ft.count(`b {"cmd":"flash_hit","rear":true}`) != 1 || ft.count(`b {"cmd":"set_hp","hp":25,"max":100}`) != 1 {
		t.Fatal("Bob's tank wasn't told about the rear hit")
	}
}

func TestSideAndFrontDamage(t *testing.T) {
	g, _, w, _, a, _ := combatGame(t, nil)
	w.set(map[string]int{"b": 0x04}, map[string]int{"b": 0x04}) // right side
	if text := shoot(g, a); text != "Hit Bob on flank" {
		t.Fatalf("got %q", text)
	}
	if hp := g.hpOf("b"); hp != 62 { // 25 × 1.5 = 37.5, rounded to even: 38
		t.Fatalf("after a side hit Bob has %d, want 62", hp)
	}
	w.set(map[string]int{"b": 0x01}, map[string]int{"b": 0x01}) // front
	shoot(g, a)
	if hp := g.hpOf("b"); hp != 37 {
		t.Fatalf("after a front hit Bob has %d, want 37", hp)
	}
}

func TestOneBarrelOnlyIsAMiss(t *testing.T) {
	g, _, w, _, a, _ := combatGame(t, nil)
	w.set(map[string]int{"b": 0x01}, map[string]int{"b": 0}) // a reflection: left barrel only
	if text := shoot(g, a); text != "Miss" {
		t.Fatalf("got %q, want Miss", text)
	}
	if hp := g.hpOf("b"); hp != 100 {
		t.Fatalf("Bob lost HP on a miss: %d", hp)
	}
}

func TestNoAckIsAMiss(t *testing.T) {
	g, ft, w, _, a, _ := combatGame(t, nil)
	w.mu.Lock()
	w.noAck = true
	w.mu.Unlock()
	if text := shoot(g, a); text != "Miss" {
		t.Fatalf("got %q, want Miss", text)
	}
	if ft.count(`a {"cmd":"ir_emit_stop"}`) == 0 {
		t.Fatal("left barrel not switched off after the missing ack")
	}
}

func TestDeathReturnToBaseAndRespawn(t *testing.T) {
	g, ft, w, _, a, b := combatGame(t, func(s *Settings) {
		s.DamagePerHit = 100
		s.ExplosionSeconds = 0.05
		s.InvulnerableSeconds = 0.3
	})
	w.set(map[string]int{"b": 0x01}, map[string]int{"b": 0x01})

	if text := shoot(g, a); text != "You killed Bob!" {
		t.Fatalf("got %q", text)
	}
	b.waitFor("you_are_dead")
	if ft.count(`b {"cmd":"motors_off"}`) == 0 || ft.count(`b {"cmd":"flash_death"}`) != 1 {
		t.Fatal("Bob's tank didn't stop and explode")
	}
	g.mu.Lock()
	pts := g.match.teamPoints
	g.mu.Unlock()
	if pts != [2]int{25, 0} {
		t.Fatalf("team points %v, want Desert 25", pts)
	}

	// Shooting the wreck: "already dead".
	if text := shoot(g, a); text != "That tank is already dead!" {
		t.Fatalf("got %q", text)
	}
	// Bob can't fire while dead.
	before := ft.count(`b {"cmd":"flash_fire"}`)
	b.send(inMsg{T: "fire"})
	if ft.count(`b {"cmd":"flash_fire"}`) != before {
		t.Fatal("a dead tank fired")
	}

	// After the explosion: return to base, motors back on.
	time.Sleep(60 * time.Millisecond)
	g.tick()
	if g.match.fighters["b"].life != returning || ft.count(`b {"cmd":"motors_on"}`) < 2 {
		t.Fatalf("Bob should be returning to base with motors on (life %v)", g.match.fighters["b"].life)
	}

	// The wrong base does nothing; his own (Jungle) base respawns him.
	g.tankMessage("b", tankReport{Cmd: "rfid", UID: "118D6A5D"}) // a Desert base
	if g.match.fighters["b"].life != returning {
		t.Fatal("respawned at the enemy's base")
	}
	g.tankMessage("b", tankReport{Cmd: "rfid", UID: "11f6025d"}) // Jungle base, any case
	b.waitFor("you_are_alive")
	if hp := g.hpOf("b"); hp != 100 || ft.count(`b {"cmd":"invuln_start"}`) != 1 {
		t.Fatalf("respawn: HP %d, invuln_start sent %d times", hp, ft.count(`b {"cmd":"invuln_start"}`))
	}

	// Invulnerable straight after: a hit does nothing.
	if text := shoot(g, a); text != "Miss" {
		t.Fatalf("hit on an invulnerable tank told %q", text)
	}
	if hp := g.hpOf("b"); hp != 100 {
		t.Fatal("invulnerable tank took damage")
	}
	time.Sleep(320 * time.Millisecond)
	g.tick()
	if ft.count(`b {"cmd":"invuln_end"}`) != 1 {
		t.Fatal("invulnerability didn't end")
	}
	if text := shoot(g, a); text != "You killed Bob!" {
		t.Fatalf("after invulnerability: %q", text)
	}
}

func TestBaseHealRefillsHp(t *testing.T) {
	g, ft, w, _, a, b := combatGame(t, nil)
	w.set(map[string]int{"b": 0x01}, map[string]int{"b": 0x01})
	shoot(g, a)
	if hp := g.hpOf("b"); hp != 75 {
		t.Fatalf("HP %d", hp)
	}
	g.tankMessage("b", tankReport{Cmd: "rfid", UID: "1168465D"})
	if msg := b.waitFor("rfid"); msg["text"] != "Hit points restored!" {
		t.Fatalf("got %v", msg)
	}
	if hp := g.hpOf("b"); hp != 100 || ft.count(`b {"cmd":"flash_heal"}`) != 1 {
		t.Fatalf("base heal: HP %d", hp)
	}
	g.tankMessage("b", tankReport{Cmd: "rfid", UID: "118D6A5D"})
	if msg := b.waitFor("rfid"); msg["text"] != "This is not your base" {
		t.Fatalf("enemy base: %v", msg)
	}
}

func TestKillPointsWinTheGame(t *testing.T) {
	g, _, w, _, a, b := combatGame(t, func(s *Settings) {
		s.DamagePerHit = 100
		s.MaxTeamPoints = 25
	})
	w.set(map[string]int{"b": 0x01}, map[string]int{"b": 0x01})
	shoot(g, a)

	st := b.state()
	for i := 0; i < 50 && st.Phase != phaseEnded; i++ {
		time.Sleep(10 * time.Millisecond)
		st = b.state()
	}
	if st.Phase != phaseEnded || st.WinnerTeam != "Desert Squad" || st.EndCode != "points" {
		t.Fatalf("phase %s, winner %q, code %q", st.Phase, st.WinnerTeam, st.EndCode)
	}
	var alice result
	for _, r := range st.Results {
		if r.Name == "Alice" {
			alice = r
		}
	}
	if alice.Kills != 1 || alice.Damage != 100 || alice.VictoryPoints != 25 {
		t.Fatalf("Alice's results: %+v", alice)
	}
	for _, r := range st.Results {
		if r.Name == "Bob" && (r.Deaths != 1 || r.Nemesis != "Alice") {
			t.Fatalf("Bob's results: %+v", r)
		}
	}
}

func TestTimeUpWinner(t *testing.T) {
	g, _, w, _, a, b := combatGame(t, nil)
	// Equal points (none), equal survivors: more damage wins.
	w.set(map[string]int{"b": 0x01}, map[string]int{"b": 0x01})
	shoot(g, a)
	g.mu.Lock()
	g.phaseEndsAt = time.Now()
	g.mu.Unlock()
	g.tick()
	st := b.state()
	if st.WinnerTeam != "Desert Squad" || st.EndCode != "time" {
		t.Fatalf("winner %q code %q", st.WinnerTeam, st.EndCode)
	}
}

func TestHostEndHasNoWinner(t *testing.T) {
	_, _, _, h, _, b := combatGame(t, nil)
	h.send(inMsg{T: "host_end"})
	st := b.state()
	if st.Phase != phaseEnded || st.WinnerTeam != "" || st.EndCode != "manual" {
		t.Fatalf("phase %s winner %q code %q", st.Phase, st.WinnerTeam, st.EndCode)
	}
}
