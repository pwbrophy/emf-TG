package main

import (
	"encoding/json"
	"strings"
	"testing"
	"time"
)

// turnLobby: a turn-based lobby with Desert-01 (a), Desert-02 (b),
// Jungle-03 (c) and Jungle-04 (d), and the shot scheduler's tank side
// scripted by an irWorld (scheduler not started).
func turnLobby(t *testing.T, tune func(*Settings)) (*game, *fakeTanks, *irWorld, *testPhone) {
	t.Helper()
	g, ft := newTestGame(t, "Desert-01", "Desert-02", "Jungle-03", "Jungle-04")
	g.settings.GameMode = modeTurns
	g.settings.IrAckTimeoutMs = 100
	g.settings.IrWindowTimeoutMs = 50
	g.settings.Turns.CountdownSeconds = 0
	g.settings.Turns.FireCooldownMs = 250
	g.settings.Turns.ShotWindowMs = 30
	g.settings.Turns.OverwatchWindowMs = 5
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
			go g.tankMessage(id, tankReport{Cmd: "ir_emit_ack"})
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
	return g, ft, w, hostUp(t, g)
}

// turnGame: Alice's army (Desert-01, Desert-02) against Bob's (Jungle-03)
// and Dan's (Jungle-04), started, with Desert's turn first.
func turnGame(t *testing.T, tune func(*Settings)) (*game, *fakeTanks, *irWorld, *testPhone, *testPhone, *testPhone, *testPhone) {
	t.Helper()
	g, ft, w, h := turnLobby(t, tune)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	a.send(inMsg{T: "pick_alliance", Alliance: "desert"})
	a.send(inMsg{T: "claim_tank", Tank: "b"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})
	b.send(inMsg{T: "pick_alliance", Alliance: "jungle"})
	d := phone(t, g, "d")
	d.send(inMsg{T: "join", Name: "Dan"})
	d.send(inMsg{T: "pick_alliance", Alliance: "jungle"})
	h.send(inMsg{T: "host_start", Force: true})
	if g.phase != phasePlaying {
		t.Fatalf("phase %s, want playing", g.phase)
	}
	forceTurn(g, 0)
	return g, ft, w, h, a, b, d
}

// forceTurn makes it alliance a's turn (the first turn is chosen at random).
func forceTurn(g *game, a int) {
	g.mu.Lock()
	defer g.mu.Unlock()
	ts := g.match.turns
	ts.order = []int{0, 1} // Desert, then Jungle
	ts.pos = a
	for _, c := range g.match.fighters {
		if !c.turn.overwatch {
			c.turn.ap = 0 // as at the end of a turn
		}
	}
	g.beginTurn(time.Now())
	g.broadcast()
}

// last is the last command sent to a tank.
func (f *fakeTanks) last(id string) string {
	f.mu.Lock()
	defer f.mu.Unlock()
	for i := len(f.sent) - 1; i >= 0; i-- {
		if msg, ok := strings.CutPrefix(f.sent[i], id+" "); ok {
			return msg
		}
	}
	return ""
}

func (g *game) fighter(id string) *combatant {
	return g.match.fighters[id]
}

func (g *game) apOf(id string) float64 {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.match.fighters[id].turn.ap
}

func apNear(a, b float64) bool { return a-b < 0.001 && b-a < 0.001 }

func TestTurnsLobbyClaiming(t *testing.T) {
	g, _, _, h := turnLobby(t, func(s *Settings) { s.Turns.MaxTanksPerArmy = 2 })
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	a.send(inMsg{T: "pick_alliance", Alliance: "desert"})
	s := a.state()
	if s.Mode != modeTurns || len(s.Me.Tanks) != 1 || s.Me.Tanks[0].ID != "a" || s.Me.Alliance != "Desert Squad" {
		t.Fatalf("joining Desert should give Alice Desert-01: mode %q, %+v", s.Mode, s.Me)
	}
	a.send(inMsg{T: "claim_tank", Tank: "c"})
	if e := a.lastError(); !strings.Contains(e, "Jungle") {
		t.Fatalf("claiming a Jungle tank: error %q", e)
	}
	a.send(inMsg{T: "claim_tank", Tank: "b"})
	if s := a.state(); len(s.Me.Tanks) != 2 {
		t.Fatalf("Alice should have 2 tanks, has %+v", s.Me.Tanks)
	}

	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})
	b.send(inMsg{T: "pick_alliance", Alliance: "desert"})
	if e := b.lastError(); !strings.Contains(e, "No free tanks") {
		t.Fatalf("Desert is full: error %q", e)
	}
	b.send(inMsg{T: "claim_tank", Tank: "a"})
	if e := b.lastError(); !strings.Contains(e, "Alice already has") {
		t.Fatalf("claiming Alice's tank: error %q", e)
	}
	b.send(inMsg{T: "pick_alliance", Alliance: "jungle"})
	b.send(inMsg{T: "claim_tank", Tank: "d"})
	if s := b.state(); len(s.Me.Tanks) != 2 {
		t.Fatalf("Bob should have both Jungle tanks: %+v", s.Me.Tanks)
	}

	// The limit.
	g.mu.Lock()
	g.settings.Turns.MaxTanksPerArmy = 1
	g.mu.Unlock()
	a.send(inMsg{T: "unclaim_tank", Tank: "b"})
	a.send(inMsg{T: "unclaim_tank", Tank: "a"})
	if e := a.lastError(); !strings.Contains(e, "at least one tank") {
		t.Fatalf("giving back the last tank: error %q", e)
	}
	a.send(inMsg{T: "claim_tank", Tank: "b"})
	if e := a.lastError(); !strings.Contains(e, "at most 1") {
		t.Fatalf("over the limit: error %q", e)
	}

	// Everyone in one alliance can't start.
	b.send(inMsg{T: "pick_alliance", Alliance: ""})
	h.send(inMsg{T: "host_start", Force: true})
	if e := h.lastError(); !strings.Contains(e, "two alliances") {
		t.Fatalf("starting with one alliance: error %q", e)
	}
	b.send(inMsg{T: "pick_alliance", Alliance: "jungle"})
	h.send(inMsg{T: "host_start", Force: true})
	if g.phase != phasePlaying {
		t.Fatalf("phase %s, want playing", g.phase)
	}
	if len(g.match.fighters) != 2 || g.match.turns == nil {
		t.Fatalf("fighters %d, turns %v", len(g.match.fighters), g.match.turns)
	}
}

func TestTurnsOnlyActiveAllianceActs(t *testing.T) {
	g, ft, _, _, a, b, _ := turnGame(t, nil)
	if ap := g.apOf("a"); !apNear(ap, 10) {
		t.Fatalf("Desert-01 AP %v, want 10", ap)
	}
	if ap := g.apOf("c"); ap != 0 {
		t.Fatalf("Jungle-03 AP %v, want 0 (not its turn)", ap)
	}
	before := ft.count(`c {"cmd":"drive"`)
	b.send(inMsg{T: "drive", L: 1, R: 1})
	b.send(inMsg{T: "turret", S: 1})
	b.send(inMsg{T: "fire"})
	if ft.count(`c {"cmd":"drive"`) != before || ft.count(`c {"cmd":"turret","speed":1`) != 0 || ft.count(`c {"cmd":"flash_fire"}`) != 0 {
		t.Fatal("Bob's tank obeyed on Desert's turn")
	}
	a.send(inMsg{T: "drive", L: 1, R: 1})
	if ft.count(`a {"cmd":"drive","l":1.000,"r":1.000}`) != 1 {
		t.Fatal("Alice's drive command didn't reach Desert-01")
	}
	s := a.state()
	if !s.Me.MyTurn || s.Turn == nil || s.Turn.AllianceIndex != 0 || s.Turn.Round != 1 || s.Me.Ap == nil || s.Me.Tank != "a" {
		t.Fatalf("Alice's view of her turn: turn %+v me %+v", s.Turn, s.Me)
	}
	if s := b.state(); s.Me.MyTurn {
		t.Fatal("Bob thinks it's his turn")
	}

	// Switching tanks: the one left stops, commands go to the new one.
	a.send(inMsg{T: "select_tank", Tank: "b"})
	if ft.count(`a {"cmd":"drive","l":0,"r":0}`) == 0 {
		t.Fatal("Desert-01 wasn't stopped when Alice switched")
	}
	if ft.count(`b {"cmd":"flash_select"}`) != 1 || ft.count(`a {"cmd":"flash_select"}`) != 0 {
		t.Fatal("only the tank Alice switched to should flash and beep")
	}
	a.send(inMsg{T: "drive", L: -1, R: 1})
	if ft.count(`b {"cmd":"drive","l":-1.000,"r":1.000}`) != 1 {
		t.Fatal("Alice's drive didn't go to Desert-02")
	}
	a.send(inMsg{T: "select_tank", Tank: "c"}) // not hers
	if s := a.state(); s.Me.Tank != "b" {
		t.Fatalf("Alice selected %q", s.Me.Tank)
	}
}

func TestTurnsDrivingSpendsActionPoints(t *testing.T) {
	g, ft, _, _, _, _, _ := turnGame(t, nil)
	g.mu.Lock()
	defer g.mu.Unlock()
	c := g.fighter("a")
	t0 := time.Now()
	c.turn.meterAt = t0
	if !g.chargeDrive(c, 1, 1, t0) {
		t.Fatal("drive refused")
	}
	g.meterAp(c, t0.Add(400*time.Millisecond))
	if !apNear(c.turn.ap, 9.6) {
		t.Fatalf("after 0.4 s at full speed AP %v, want 9.6", c.turn.ap)
	}
	// No more commands: the tracks stop 500 ms after the last one.
	g.meterAp(c, t0.Add(3*time.Second))
	if !apNear(c.turn.ap, 9.5) || c.turn.driveRate != 0 {
		t.Fatalf("after the watchdog AP %v (rate %v), want 9.5", c.turn.ap, c.turn.driveRate)
	}
	// One track: half the cost.
	t1 := t0.Add(4 * time.Second)
	g.chargeDrive(c, 1, 0, t1)
	g.meterAp(c, t1.Add(200*time.Millisecond))
	if !apNear(c.turn.ap, 9.4) {
		t.Fatalf("one track for 0.2 s: AP %v, want 9.4", c.turn.ap)
	}

	// Running out: the tank is stopped and further driving refused.
	now := time.Now()
	c.turn.ap, c.turn.driveRate = 0.2, 1
	c.turn.meterAt, c.turn.driveAt = now.Add(-500*time.Millisecond), now // driving for the last 0.5 s
	g.mu.Unlock()
	g.tick(200 * time.Millisecond) // meters up to now: more than 0.2 s
	g.mu.Lock()
	if c.turn.ap != 0 || c.turn.driveRate != 0 {
		t.Fatalf("AP %v rate %v, want both 0", c.turn.ap, c.turn.driveRate)
	}
	before := ft.count(`a {"cmd":"drive","l":1.000`)
	if g.chargeDrive(c, 1, 1, time.Now()) {
		t.Fatal("drive with no AP was allowed")
	}
	if !g.chargeDrive(c, 0, 0, time.Now()) {
		t.Fatal("stopping with no AP was refused")
	}
	if ft.count(`a {"cmd":"drive","l":1.000`) != before {
		t.Fatal("a drive command was sent with no AP")
	}
}

func TestTurnsFiringCostsActionPointsAndListensLonger(t *testing.T) {
	g, ft, w, _, a, _, _ := turnGame(t, nil)
	stop := make(chan struct{})
	go g.ir.run(stop)
	defer close(stop)
	w.set(map[string]int{}, map[string]int{})
	if text := shoot(g, a); text != "Miss" {
		t.Fatalf("first shot: %q", text)
	}
	if ap := g.apOf("a"); !apNear(ap, 6) {
		t.Fatalf("after one shot AP %v, want 6", ap)
	}
	if ft.count(`"ir_listen_window","ms":30`) == 0 {
		t.Fatal("a turn-based shot should listen for Turns.ShotWindowMs (30)")
	}
	shoot(g, a)
	if text := shoot(g, a); text != "Not enough action points" {
		t.Fatalf("third shot with 2 AP: %q", text)
	}
	if ap := g.apOf("a"); !apNear(ap, 2) {
		t.Fatalf("AP %v, want 2", ap)
	}
}

func TestRealtimeShotsKeepTheirWindow(t *testing.T) {
	g, ft, w, _, a, _ := combatGame(t, func(s *Settings) { s.Turns.ShotWindowMs = 30 })
	w.set(map[string]int{}, map[string]int{})
	shoot(g, a)
	if ft.count(`"ir_listen_window","ms":5}`) == 0 || ft.count(`"ms":30`) != 0 {
		t.Fatal("a realtime shot should listen for IrWindowMs (5)")
	}
}

func TestTurnsEndTurnHandoverAndRounds(t *testing.T) {
	g, ft, _, h, a, b, d := turnGame(t, func(s *Settings) { s.Turns.HandoverSeconds = 1 })
	a.send(inMsg{T: "drive", L: 1, R: 1})
	a.send(inMsg{T: "end_turn", On: true})
	if ft.count(`a {"cmd":"drive","l":0,"r":0}`) == 0 {
		t.Fatal("ending the turn should stop Alice's tanks")
	}
	a.send(inMsg{T: "end_turn", On: false}) // changed her mind
	g.tick(200 * time.Millisecond)
	if g.match.turns.handover {
		t.Fatal("the turn ended after END TURN was taken back")
	}
	a.send(inMsg{T: "end_turn", On: true})
	g.tick(200 * time.Millisecond)
	s := h.state()
	if !g.match.turns.handover || s.Turn == nil || !s.Turn.Handover || s.Turn.Next != "Jungle Squad" {
		t.Fatalf("should be handing over to Jungle: %+v", s.Turn)
	}
	a.send(inMsg{T: "drive", L: 1, R: 1})
	if ft.count(`a {"cmd":"drive","l":1.000`) != 1 {
		t.Fatal("Alice drove during the handover")
	}
	if ap := g.apOf("a"); ap != 0 {
		t.Fatalf("unused AP %v should be lost", ap)
	}
	g.mu.Lock()
	g.match.turns.handoverUntil = time.Now()
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	if ts := g.match.turns; ts.handover || ts.active != 1 || ts.round != 1 {
		t.Fatalf("Jungle's turn in round 1: active %d round %d handover %v", ts.active, ts.round, ts.handover)
	}
	if ap := g.apOf("c"); !apNear(ap, 10) {
		t.Fatalf("Jungle-03 AP %v, want 10", ap)
	}
	// Both Jungle armies must be done.
	b.send(inMsg{T: "end_turn", On: true})
	g.tick(200 * time.Millisecond)
	if g.match.turns.handover {
		t.Fatal("the turn ended before Dan was done")
	}
	if s := b.state(); !s.Me.TurnDone || len(s.Turn.Armies) != 3 {
		t.Fatalf("Bob's view: %+v / %+v", s.Me, s.Turn)
	}
	d.send(inMsg{T: "end_turn", On: true})
	g.tick(200 * time.Millisecond)
	g.mu.Lock()
	g.match.turns.handoverUntil = time.Now()
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	if ts := g.match.turns; ts.active != 0 || ts.round != 2 {
		t.Fatalf("round 2 should start with Desert: active %d round %d", ts.active, ts.round)
	}
}

func TestTurnsTimerAndPause(t *testing.T) {
	g, _, _, h, _, _, _ := turnGame(t, func(s *Settings) { s.Turns.TurnSeconds = 60 })
	h.send(inMsg{T: "host_pause", On: true})
	g.mu.Lock()
	g.pausedAt = g.pausedAt.Add(-10 * time.Second) // paused 10 s ago
	left := g.match.turns.endsAt.Sub(time.Now())
	g.mu.Unlock()
	h.send(inMsg{T: "host_pause", On: false})
	if extra := g.match.turns.endsAt.Sub(time.Now()) - left; extra < 9*time.Second {
		t.Fatalf("the pause should add its 10 s to the turn, added %v", extra)
	}
	g.mu.Lock()
	g.match.turns.endsAt = time.Now()
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	if !g.match.turns.handover {
		t.Fatal("the turn should end when its time is up")
	}
}

func TestTurnsDestroyedTanksStayOutAndEliminationWins(t *testing.T) {
	g, ft, _, h, _, b, d := turnGame(t, func(s *Settings) { s.Turns.DamagePerHit = 100 })
	h.send(inMsg{T: "host_damage", Tank: "c", Dir: "N"})
	if l := g.fighter("c").life; l != destroyed {
		t.Fatalf("Jungle-03 is %v, want destroyed", l)
	}
	if m := b.waitFor("tank_destroyed"); m["tank"] != "c" {
		t.Fatalf("Bob was told %v", m)
	}
	b.waitFor("you_are_dead") // his only tank
	if ft.count(`c {"cmd":"motors_on"}`) > 1 {
		t.Fatal("a destroyed tank got its motors back")
	}
	g.mu.Lock()
	g.match.fighters["c"].diedAt = time.Now().Add(-time.Hour)
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	if l := g.fighter("c").life; l != destroyed {
		t.Fatalf("Jungle-03 came back as %v", l)
	}
	if g.phase != phasePlaying {
		t.Fatal("the game ended with Dan still in it")
	}
	h.send(inMsg{T: "host_damage", Tank: "d", Dir: "N"})
	d.waitFor("you_are_dead")
	if g.phase != phaseEnded || g.match.endCode != "elimination" || g.match.winner != 0 {
		t.Fatalf("phase %s, end %q, winner %d", g.phase, g.match.endCode, g.match.winner)
	}
}

func TestTurnsLosingATankSelectsTheNext(t *testing.T) {
	g, _, _, h, a, _, _ := turnGame(t, func(s *Settings) { s.Turns.DamagePerHit = 100 })
	h.send(inMsg{T: "host_damage", Tank: "a", Dir: "N"})
	a.waitFor("tank_destroyed")
	if s := a.state(); s.Me.Tank != "b" || s.Me.Tanks[0].Life != "destroyed" {
		t.Fatalf("Alice should now control Desert-02: %+v", s.Me)
	}
	_ = g
}

func TestTurnsRoundLimit(t *testing.T) {
	g, _, _, _, a, b, d := turnGame(t, func(s *Settings) { s.Turns.MaxRounds = 1; s.Turns.HandoverSeconds = 0 })
	a.send(inMsg{T: "end_turn", On: true})
	g.tick(200 * time.Millisecond)
	if g.match.turns.active != 1 {
		t.Fatal("Jungle's turn should follow")
	}
	b.send(inMsg{T: "end_turn", On: true})
	d.send(inMsg{T: "end_turn", On: true})
	g.tick(200 * time.Millisecond)
	if g.phase != phaseEnded || g.match.endCode != "rounds" {
		t.Fatalf("phase %s end %q, want ended by the round limit", g.phase, g.match.endCode)
	}
}

func TestTurnsCapturePointsScoreEachTurn(t *testing.T) {
	g, _, _, h, a, _, _ := turnGame(t, func(s *Settings) { s.Turns.CapturePointsPerTurn = 7 })
	h.send(inMsg{T: "host_capture", Point: 0}) // Desert holds North
	h.send(inMsg{T: "host_capture", Point: 1})
	h.send(inMsg{T: "host_capture", Point: 1}) // Jungle holds Centre
	if s := a.state(); s.AlliancePoints[0] != 0 {
		t.Fatalf("points before a turn starts: %v", s.AlliancePoints)
	}
	forceTurn(g, 1)
	forceTurn(g, 0)
	if s := a.state(); s.AlliancePoints[0] != 7 || s.AlliancePoints[1] != 7 {
		t.Fatalf("points %v, want 7 each", s.AlliancePoints)
	}
}

func TestTurnsBaseRepairUsesActionPoints(t *testing.T) {
	g, _, _, h, a, _, _ := turnGame(t, nil)
	h.send(inMsg{T: "host_damage", Tank: "a", Dir: "N"})
	g.tankMessage("a", tankReport{Cmd: "rfid", UID: "118D6A5D"}) // a Desert base tag
	if hp, ap := g.hpOf("a"), g.apOf("a"); hp != 100 || ap != 0 {
		t.Fatalf("after repairing HP %d AP %v, want 100 and 0", hp, ap)
	}
	if m := a.waitFor("rfid"); !strings.Contains(m["text"].(string), "Repaired") {
		t.Fatalf("Alice was told %v", m)
	}
}

func TestTurnsRulesAreSeparate(t *testing.T) {
	g, _, _, h, a, _, _ := turnGame(t, func(s *Settings) { s.Turns.DamagePerHit = 10; s.Turns.MaxHp = 50 })
	if hp := g.hpOf("c"); hp != 50 {
		t.Fatalf("turn-based max HP: %d, want 50", hp)
	}
	h.send(inMsg{T: "host_damage", Tank: "c", Dir: "N"})
	if hp := g.hpOf("c"); hp != 40 {
		t.Fatalf("turn-based damage: HP %d, want 40", hp)
	}
	if g.settings.DamagePerHit != 25 || g.settings.MaxHp != 100 {
		t.Fatalf("realtime rules changed: %d / %d", g.settings.DamagePerHit, g.settings.MaxHp)
	}
	// Editing the realtime rules mid-game doesn't touch this game.
	st := g.settings
	st.DamagePerHit = 99
	h.send(inMsg{T: "host_settings", Settings: &st})
	h.send(inMsg{T: "host_damage", Tank: "c", Dir: "N"})
	if hp := g.hpOf("c"); hp != 30 {
		t.Fatalf("after changing the realtime damage, HP %d, want 30", hp)
	}
	if g.settings.Turns.DamagePerHit != 10 || g.settings.GameMode != modeTurns {
		t.Fatal("the turn-based rules or the mode changed")
	}
	if s := a.state(); s.Me.MaxHp != 50 || s.FireCooldownMs != 250 {
		t.Fatalf("Alice sees max HP %d cooldown %d", s.Me.MaxHp, s.FireCooldownMs)
	}
}

// keepDriving sends Alice's drive commands for d (a moving target).
func keepDriving(p *testPhone, d time.Duration) {
	end := time.Now().Add(d)
	for time.Now().Before(end) {
		p.send(inMsg{T: "drive", L: 1, R: 1})
		time.Sleep(40 * time.Millisecond)
	}
}

// onOverwatch puts Bob's Jungle-03 on overwatch during Jungle's turn, then
// gives Desert the turn.
func onOverwatch(t *testing.T, g *game, b *testPhone) {
	t.Helper()
	forceTurn(g, 1)
	b.send(inMsg{T: "overwatch", On: true})
	if !g.fighter("c").turn.overwatch {
		t.Fatalf("Jungle-03 isn't on overwatch: %q", b.lastError())
	}
	forceTurn(g, 0)
	if !g.fighter("c").turn.overwatch || !g.ir.probing.Load() {
		t.Fatal("overwatch should last into Desert's turn")
	}
}

func TestOverwatchFiresOnceAtAMovingEnemy(t *testing.T) {
	g, ft, w, _, a, b, _ := turnGame(t, nil)
	onOverwatch(t, g, b)
	w.set(map[string]int{"a": 0x01}, map[string]int{"a": 0x01}) // Desert-01 is in its line of fire
	stop := make(chan struct{})
	go g.ir.run(stop)
	defer close(stop)

	// Standing still: no probes.
	time.Sleep(150 * time.Millisecond)
	if n := ft.count(`c {"cmd":"ir_emit_left"}`); n != 0 {
		t.Fatalf("%d probes while nobody moved", n)
	}
	keepDriving(a, 600*time.Millisecond)
	if hp := g.hpOf("a"); hp != 75 {
		t.Fatalf("Desert-01 HP %d, want 75 (one overwatch hit)", hp)
	}
	// The overwatch tank shows its shot (lights and buzzer), the target its hit.
	if ft.count(`c {"cmd":"flash_fire"}`) != 1 || ft.count(`a {"cmd":"flash_hit"`) != 1 {
		t.Fatalf("flash_fire to the shooter %d, flash_hit to the target %d",
			ft.count(`c {"cmd":"flash_fire"}`), ft.count(`a {"cmd":"flash_hit"`))
	}
	if m := b.waitFor("fire_result"); !strings.HasPrefix(m["text"].(string), "Overwatch: Hit") {
		t.Fatalf("Bob was told %v", m)
	}
	c := g.fighter("c")
	if c.turn.overwatch || c.turn.ap != 0 || g.ir.probing.Load() {
		t.Fatalf("after its shot: overwatch %v AP %v", c.turn.overwatch, c.turn.ap)
	}
	// It had AP for two shots, but it fires once.
	keepDriving(a, 300*time.Millisecond)
	if hp := g.hpOf("a"); hp != 75 || ft.count(`c {"cmd":"flash_fire"}`) != 1 {
		t.Fatalf("overwatch fired again: HP %d", hp)
	}
}

func TestOverwatchIgnoresStillTargetsAndClearsOnOwnTurn(t *testing.T) {
	g, ft, w, _, a, b, d := turnGame(t, nil)
	// Bob and Dan both on overwatch: they take turns probing.
	forceTurn(g, 1)
	b.send(inMsg{T: "overwatch", On: true})
	d.send(inMsg{T: "overwatch", On: true})
	forceTurn(g, 0)
	w.set(map[string]int{}, map[string]int{}) // nobody in their sights
	stop := make(chan struct{})
	go g.ir.run(stop)
	defer close(stop)
	keepDriving(a, 400*time.Millisecond)
	if ft.count(`c {"cmd":"ir_emit_left"}`) == 0 || ft.count(`d {"cmd":"ir_emit_left"}`) == 0 {
		t.Fatal("both overwatch tanks should probe while Alice moves")
	}
	if ft.count(`c {"cmd":"flash_fire"}`) != 0 {
		t.Fatal("a probe that missed showed a shot")
	}
	// A tank in its sights that isn't moving can't be hit.
	w.set(map[string]int{"b": 0x01}, map[string]int{"b": 0x01})
	time.Sleep(100 * time.Millisecond)
	if hp := g.hpOf("b"); hp != 100 {
		t.Fatalf("a still tank was hit: HP %d", hp)
	}
	forceTurn(g, 1)
	if g.fighter("c").turn.overwatch || !apNear(g.apOf("c"), 10) {
		t.Fatal("overwatch should end (and AP refill) at the start of its own turn")
	}
}

func TestOverwatchSwitchedOff(t *testing.T) {
	g, _, _, _, _, b, _ := turnGame(t, func(s *Settings) { s.Turns.Overwatch = false })
	forceTurn(g, 1)
	b.send(inMsg{T: "overwatch", On: true})
	if e := b.lastError(); !strings.Contains(e, "switched off") || g.fighter("c").turn.overwatch {
		t.Fatalf("overwatch with the toggle off: error %q", e)
	}
	g.mu.Lock()
	s := g.snapshot("b")
	g.mu.Unlock()
	if s.TurnRules == nil || s.TurnRules.Overwatch {
		t.Fatal("the phones should be told overwatch is off")
	}
}

func TestShotsGoBeforeProbes(t *testing.T) {
	g, ft, w, _, a, b, _ := turnGame(t, nil)
	onOverwatch(t, g, b)
	w.set(map[string]int{}, map[string]int{})
	a.send(inMsg{T: "drive", L: 1, R: 1}) // a moving target: probes are wanted
	a.send(inMsg{T: "fire"})              // and a shot is waiting
	stop := make(chan struct{})
	go g.ir.run(stop)
	defer close(stop)
	a.waitFor("fire_result")
	ft.mu.Lock()
	first := ""
	for _, s := range ft.sent {
		if strings.Contains(s, `"ir_emit_left"`) {
			first = s
			break
		}
	}
	ft.mu.Unlock()
	if !strings.HasPrefix(first, "a ") {
		t.Fatalf("the first barrel lit was %q, want Alice's shot", first)
	}
}

// rawState is the newest state message a phone was sent, as JSON.
func rawState(p *testPhone) map[string]any {
	var st map[string]any
	for {
		select {
		case b := <-p.c.out:
			var m map[string]any
			_ = json.Unmarshal(b, &m)
			if m["t"] == "state" {
				st = m
			}
		default:
			return st
		}
	}
}

func TestRealtimeSnapshotsHaveNoTurnFields(t *testing.T) {
	_, _, _, _, a, _ := combatGame(t, nil)
	a.send(inMsg{T: "stabilise", On: false}) // a fresh snapshot
	st := rawState(a)
	for _, k := range []string{"mode", "turn", "turnRules"} {
		if _, ok := st[k]; ok {
			t.Errorf("realtime snapshot has %q", k)
		}
	}
	me := st["me"].(map[string]any)
	for _, k := range []string{"ap", "apMax", "tanks", "overwatch", "myTurn"} {
		if _, ok := me[k]; ok {
			t.Errorf("realtime me has %q", k)
		}
	}
	for _, tv := range st["tanks"].([]any) {
		if _, ok := tv.(map[string]any)["ap"]; ok {
			t.Error("realtime tank has ap")
		}
	}
}

func TestChangingModeClearsLobbyChoices(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01", "Jungle-02")
	h := hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	a.send(inMsg{T: "pick_tank", Tank: "a"})
	st := g.settings
	st.GameMode = modeTurns
	h.send(inMsg{T: "host_settings", Settings: &st})
	if s := a.state(); s.Me.Tank != "" || s.Mode != modeTurns {
		t.Fatalf("after switching to turn-based Alice still has %q (mode %q)", s.Me.Tank, s.Mode)
	}
}

func TestTurnsShieldRechargeOncePerTurn(t *testing.T) {
	g, ft, _, h, a, _, _ := turnGame(t, nil) // defaults: 20 % for 5 AP
	a.send(inMsg{T: "recharge"})
	if e := a.lastError(); !strings.Contains(e, "already full") {
		t.Fatalf("recharging a full shield: error %q", e)
	}
	h.send(inMsg{T: "host_damage", Tank: "a", Dir: "S"}) // 25 × 3 = 75: HP 25
	a.send(inMsg{T: "recharge"})
	if hp, ap := g.hpOf("a"), g.apOf("a"); hp != 45 || !apNear(ap, 5) {
		t.Fatalf("after recharging HP %d AP %v, want 45 and 5", hp, ap)
	}
	if ft.count(`a {"cmd":"flash_heal"}`) != 1 || ft.count(`a {"cmd":"set_hp","hp":45,`) != 1 || !a.state().Me.Recharged {
		t.Fatal("the tank should flash and show its HP, and the phone know it recharged")
	}
	a.send(inMsg{T: "recharge"})
	if e := a.lastError(); !strings.Contains(e, "already recharged") || g.hpOf("a") != 45 {
		t.Fatalf("a second recharge in one turn: error %q, HP %d", e, g.hpOf("a"))
	}
	// Desert-02 without the points.
	a.send(inMsg{T: "select_tank", Tank: "b"})
	h.send(inMsg{T: "host_damage", Tank: "b", Dir: "N"})
	g.mu.Lock()
	g.fighter("b").turn.ap = 4
	g.mu.Unlock()
	a.send(inMsg{T: "recharge"})
	if e := a.lastError(); !strings.Contains(e, "needs 5 action points") || g.hpOf("b") != 75 {
		t.Fatalf("recharging without the points: error %q, HP %d", e, g.hpOf("b"))
	}
	// The next turn it can recharge again, up to full.
	forceTurn(g, 1)
	forceTurn(g, 0)
	a.send(inMsg{T: "recharge"})
	if hp := g.hpOf("b"); hp != 95 {
		t.Fatalf("Desert-02 HP %d after recharging in a new turn, want 95", hp)
	}
}

func TestTurnsShieldRechargeSwitchedOff(t *testing.T) {
	g, _, _, h, a, _, _ := turnGame(t, func(s *Settings) { s.Turns.ShieldRechargePct = 0 })
	h.send(inMsg{T: "host_damage", Tank: "a", Dir: "N"})
	a.send(inMsg{T: "recharge"})
	if e := a.lastError(); !strings.Contains(e, "switched off") || g.hpOf("a") != 75 {
		t.Fatalf("error %q, HP %d", e, g.hpOf("a"))
	}
	g.mu.Lock()
	g.broadcast()
	g.mu.Unlock()
	if r, _ := rawState(a)["turnRules"].(map[string]any); r == nil || r["rechargePct"] != 0.0 {
		t.Fatalf("the phones should be told recharging is off: %v", r)
	}
}

func TestTurnsApShowsOnTheTankDuringItsTurn(t *testing.T) {
	g, ft, _, _, a, _, _ := turnGame(t, func(s *Settings) { s.Turns.HandoverSeconds = 1 })
	g.tick(200 * time.Millisecond)
	// Desert's turn: Alice's selected tank (a) shows its AP in green, her
	// other tank (b) stays blue, Jungle's tanks go red-orange.
	if ft.count(`a {"cmd":"set_ap","ap":10.0,"max":10}`) != 1 {
		t.Fatal("Alice's selected tank should show 10 AP")
	}
	if ft.count(`b {"cmd":"set_ap"`) != 0 {
		t.Fatal("Alice's other tank should keep its blue HP bar")
	}
	for _, id := range []string{"c", "d"} {
		if ft.count(id+` {"cmd":"set_ap","max":0,"wait":1}`) != 1 {
			t.Fatalf("Jungle tank %s should show its HP in red-orange", id)
		}
	}
	g.tick(200 * time.Millisecond)
	if ft.count(`a {"cmd":"set_ap"`) != 1 {
		t.Fatal("set_ap should only be sent when the bar changes")
	}
	g.mu.Lock()
	g.fighter("a").turn.ap = 6.04
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	if ft.count(`a {"cmd":"set_ap","ap":6.0,"max":10}`) != 1 {
		t.Fatal("the bar should follow the AP")
	}
	// Switching: the AP bar moves to b (before its select flash), a goes blue.
	a.send(inMsg{T: "select_tank", Tank: "b"})
	if ft.count(`b {"cmd":"set_ap","ap":10.0,"max":10}`) != 1 || ft.count(`a {"cmd":"set_ap","max":0}`) != 1 {
		t.Fatal("the AP bar should move to the tank Alice selected")
	}
	if ft.last("b") != `{"cmd":"flash_select"}` {
		t.Fatal("the select flash should come after the bar it uncovers")
	}
	// Handover: Jungle is next, so its tanks go blue and Desert's red-orange.
	a.send(inMsg{T: "end_turn", On: true})
	g.tick(200 * time.Millisecond)
	if ft.count(`c {"cmd":"set_ap","max":0}`) != 1 || ft.count(`a {"cmd":"set_ap","max":0,"wait":1}`) != 1 ||
		ft.count(`b {"cmd":"set_ap","max":0,"wait":1}`) != 1 {
		t.Fatal("in the handover the next alliance should go blue and the other red-orange")
	}
}
