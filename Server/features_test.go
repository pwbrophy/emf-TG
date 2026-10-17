package main

import (
	"encoding/json"
	"strings"
	"testing"
	"time"
)

// Capture points: scanning a point's tag takes it; held points score one
// team point every CaptureSeconds / (points held); the captor gets the VP.
func TestCapturePointScoring(t *testing.T) {
	g, ft, _, _, a, _ := combatGame(t, func(s *Settings) { s.CaptureSeconds = 3; s.TeamPointsPerKill = 25 })
	g.tankMessage("a", tankReport{Cmd: "rfid", UID: "110e8c5d"}) // North
	if m := a.waitFor("rfid"); m["text"] != "You have captured North!" {
		t.Fatalf("capture message %v", m["text"])
	}
	if ft.count(`a {"cmd":"flash_capture"}`) != 1 {
		t.Fatal("no capture flash on the tank")
	}
	g.tankMessage("a", tankReport{Cmd: "rfid", UID: "110E8C5D"})
	if m := a.waitFor("rfid"); m["text"] != "You already captured this point!" {
		t.Fatalf("second scan: %v", m["text"])
	}

	g.mu.Lock()
	g.tickCapturePoints(3 * time.Second) // one point held: 1 per 3 s
	pts := g.match.teamPoints
	g.mu.Unlock()
	if pts != [2]int{1, 0} {
		t.Fatalf("after 3 s holding one point: %v", pts)
	}

	g.tankMessage("a", tankReport{Cmd: "rfid", UID: "11E0A45D"}) // Centre
	g.mu.Lock()
	g.tickCapturePoints(3 * time.Second) // two points: 1 per 1.5 s
	pts = g.match.teamPoints
	g.mu.Unlock()
	if pts != [2]int{3, 0} {
		t.Fatalf("after 3 s holding two points: %v", pts)
	}

	g.mu.Lock()
	g.endGame("manual")
	res := g.results
	g.mu.Unlock()
	for _, r := range res {
		if r.Name == "Alice" && (r.Captures != 2 || r.VictoryPoints != 3) {
			t.Fatalf("Alice's results: %+v", r)
		}
	}
}

// Returning (dead) tanks can't capture.
func TestDeadTanksCantCapture(t *testing.T) {
	g, _, _, _, _, _ := combatGame(t, nil)
	g.mu.Lock()
	g.match.fighters["a"].life = returning
	g.mu.Unlock()
	g.tankMessage("a", tankReport{Cmd: "rfid", UID: "110E8C5D"})
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.match.cpOwners[0] != -1 {
		t.Fatal("a returning tank captured North")
	}
}

// The host can cycle a point's owner by hand.
func TestHostCapture(t *testing.T) {
	g, _, _, h, _, _ := combatGame(t, nil)
	h.send(inMsg{T: "host_capture", Point: 2})
	h.send(inMsg{T: "host_capture", Point: 2})
	if s := h.state(); s.CapturePoints[2].Owner != 1 {
		t.Fatalf("South owner %d, want 1 after two presses", s.CapturePoints[2].Owner)
	}
	h.send(inMsg{T: "host_capture", Point: 2})
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.match.cpOwners[2] != -1 {
		t.Fatal("third press should free the point")
	}
}

// 2-player tanks: the driver lets a gunner join; only the driver drives and
// only the gunner shoots; kills go to the gunner, deaths to both.
func TestTwoPlayerCrew(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01", "Jungle-02")
	g.settings.TwoPlayerTanks = true
	h := hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	c := phone(t, g, "c")
	c.send(inMsg{T: "join", Name: "Cat"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})

	a.send(inMsg{T: "pick_tank", Tank: "a"})
	c.send(inMsg{T: "join_gunner", Tank: "a"})
	if !strings.Contains(c.lastError(), "isn't taking a gunner") {
		t.Fatal("joined as gunner without the driver's OK")
	}
	a.send(inMsg{T: "allow_gunner", On: true})
	c.send(inMsg{T: "join_gunner", Tank: "a"})
	if s := c.state(); s.Me.Role != roleGunner || s.Me.Partner != "Alice" || s.Me.Tank != "a" {
		t.Fatalf("Cat should be Alice's gunner: %+v", s.Me)
	}
	if s := a.state(); s.Me.Role != roleDriver || s.Me.Partner != "Cat" {
		t.Fatalf("Alice should be driving with Cat: %+v", s.Me)
	}
	b.send(inMsg{T: "pick_tank", Tank: "b"})

	h.send(inMsg{T: "host_start"})
	g.mu.Lock()
	g.phaseEndsAt = time.Now()
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	if g.phase != phasePlaying {
		t.Fatalf("phase %s", g.phase)
	}

	before := ft.count(`a {"cmd":"drive"`)
	c.send(inMsg{T: "drive", L: 1, R: 1})
	if ft.count(`a {"cmd":"drive"`) != before {
		t.Fatal("the gunner drove the tank")
	}
	a.send(inMsg{T: "drive", L: 1, R: 1})
	if ft.count(`a {"cmd":"drive"`) != before+1 {
		t.Fatal("the driver couldn't drive")
	}
	a.send(inMsg{T: "fire"})
	if ft.count(`a {"cmd":"flash_fire"}`) != 0 {
		t.Fatal("the driver fired")
	}
	c.send(inMsg{T: "turret", S: 0.5})
	if ft.count(`a {"cmd":"turret","speed":0.500}`) != 1 {
		t.Fatal("the gunner couldn't turn the turret")
	}

	// Cat's tank kills Bob; then Bob's kills Alice and Cat's.
	g.mu.Lock()
	m := g.match
	g.applyHit(m.fighters["a"], m.fighters["b"], 0x10, "S")
	g.applyHit(m.fighters["a"], m.fighters["b"], 0x10, "S")
	g.applyHit(m.fighters["b"], m.fighters["a"], 0x10, "S")
	g.applyHit(m.fighters["b"], m.fighters["a"], 0x10, "S")
	g.endGame("manual")
	res := map[string]result{}
	for _, r := range g.results {
		res[r.Name] = r
	}
	g.mu.Unlock()
	if res["Cat"].Kills != 1 || res["Alice"].Kills != 0 {
		t.Fatalf("the kill should be Cat's: %+v %+v", res["Cat"], res["Alice"])
	}
	if res["Cat"].Deaths != 1 || res["Alice"].Deaths != 1 || res["Bob"].Deaths != 1 {
		t.Fatalf("deaths: Alice %d Cat %d Bob %d", res["Alice"].Deaths, res["Cat"].Deaths, res["Bob"].Deaths)
	}
	if res["Cat"].Role != roleGunner || res["Alice"].Role != roleDriver {
		t.Fatalf("roles in results: %q %q", res["Alice"].Role, res["Cat"].Role)
	}
}

// When a driver leaves the lobby their gunner takes the tank; swap works.
func TestCrewChanges(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01")
	g.settings.TwoPlayerTanks = true
	hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	c := phone(t, g, "c")
	c.send(inMsg{T: "join", Name: "Cat"})
	if s := c.state(); s.Me.Status != "lobby" {
		t.Fatalf("1 tank with 2-player on should take 2 players: Cat %s", s.Me.Status)
	}
	a.send(inMsg{T: "pick_tank", Tank: "a"})
	a.send(inMsg{T: "allow_gunner", On: true})
	c.send(inMsg{T: "join_gunner", Tank: "a"})
	c.send(inMsg{T: "swap_roles"})
	if s := c.state(); s.Me.Role != roleDriver {
		t.Fatalf("after swapping Cat should drive: %+v", s.Me)
	}
	if s := a.state(); s.Me.Role != roleGunner {
		t.Fatalf("after swapping Alice should be gunner: %+v", s.Me)
	}
	c.send(inMsg{T: "leave"})
	if s := a.state(); s.Me.Tank != "a" || s.Me.Role != "" {
		t.Fatalf("Alice should have the tank to herself: %+v", s.Me)
	}
}

// Pausing stops the clock, the motors and the damage; resuming carries on.
func TestPause(t *testing.T) {
	g, ft, _, h, a, _ := combatGame(t, nil)
	h.send(inMsg{T: "host_pause", On: true})
	if s := a.state(); !s.Paused {
		t.Fatal("not paused")
	}
	if ft.count(`a {"cmd":"motors_off"}`) == 0 {
		t.Fatal("motors left on")
	}
	left := g.remaining()
	time.Sleep(300 * time.Millisecond)
	if g.remaining() != left {
		t.Fatal("the clock kept running")
	}
	g.mu.Lock()
	dmg, _ := g.applyHit(g.match.fighters["b"], g.match.fighters["a"], 1, "N")
	g.mu.Unlock()
	if dmg != 0 {
		t.Fatal("damage while paused")
	}
	before := ft.count(`a {"cmd":"drive"`)
	a.send(inMsg{T: "drive", L: 1, R: 1})
	if ft.count(`a {"cmd":"drive"`) != before {
		t.Fatal("drove while paused")
	}
	h.send(inMsg{T: "host_pause", On: false})
	if ft.count(`a {"cmd":"motors_on"}`) < 2 {
		t.Fatal("motors not back on")
	}
	if r := g.remaining(); r > left || r < left-200*time.Millisecond {
		t.Fatalf("time left %v, want about %v", r, left)
	}
}

func TestHostDamageAndHeal(t *testing.T) {
	g, ft, _, h, _, _ := combatGame(t, nil)
	h.send(inMsg{T: "host_damage", Tank: "b", Dir: "S"})
	if hp := g.hpOf("b"); hp != 25 {
		t.Fatalf("rear test hit: hp %d, want 25", hp)
	}
	if ft.count(`b {"cmd":"flash_hit","rear":true}`) != 1 {
		t.Fatal("no rear hit flash")
	}
	h.send(inMsg{T: "host_damage", Tank: "b", Dir: "E"})
	g.mu.Lock()
	life := g.match.fighters["b"].life
	g.mu.Unlock()
	if life != exploding {
		t.Fatal("second test hit should destroy Bob")
	}
	h.send(inMsg{T: "host_heal", Tank: "b"})
	g.mu.Lock()
	c := g.match.fighters["b"]
	g.mu.Unlock()
	if c.life != alive || c.hp != 100 {
		t.Fatalf("heal should resurrect: %v %d", c.life, c.hp)
	}
	// Test hits score nothing.
	if s := h.state(); s.TeamPoints != [2]int{0, 0} {
		t.Fatalf("test kill scored: %v", s.TeamPoints)
	}
}

func TestHostTankTools(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01")
	h := hostUp(t, g)
	h.send(inMsg{T: "host_tank_flags", Tank: "a", Flags: &tankFlags{HFlip: true}})
	if ft.count(`a {"cmd":"set_drive_config"}`) != 1 {
		t.Fatal("flags not sent")
	}
	h.send(inMsg{T: "host_tank_rename", Tank: "a", Name: "Jungle-07"})
	s := h.state()
	if s.Tanks[0].Name != "Jungle-07" || s.Tanks[0].Team != "Jungle Squad" || !s.Tanks[0].Flags.HFlip {
		t.Fatalf("after rename: %+v", s.Tanks[0])
	}
	h.send(inMsg{T: "host_tank_team", Tank: "a", Team: 0})
	if s := h.state(); s.Tanks[0].Team != "Desert Squad" || !s.Tanks[0].TeamOverride {
		t.Fatalf("team override: %+v", s.Tanks[0])
	}
	h.send(inMsg{T: "host_tank_team", Tank: "a", Team: -1})
	if s := h.state(); s.Tanks[0].Team != "Jungle Squad" {
		t.Fatalf("team back from name: %+v", s.Tanks[0])
	}
	h.send(inMsg{T: "host_tank_ping", Tank: "a"})
	g.tankMessage("a", tankReport{Cmd: "pong"})
	g.mu.Lock()
	_, ok := g.pingMs["a"]
	g.mu.Unlock()
	if !ok {
		t.Fatal("no ping time")
	}
}

func TestTuningAndPresets(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01")
	h := hostUp(t, g)
	g.tankConnected("a")
	if ft.count(`a {"cmd":"set_physics"`) != 1 || ft.count(`a {"cmd":"set_buzzer","enabled":true}`) != 1 {
		t.Fatal("tuning not sent on connect")
	}
	if ft.count("set_video") != 0 {
		t.Fatal("video settings sent on connect though unchanged")
	}

	s := g.settings
	s.Tuning.DriveMin = 0.6
	s.Tuning.VideoQuality = 30
	h.send(inMsg{T: "host_settings", Settings: &s})
	if ft.count(`"drive_min":0.6`) != 1 || ft.count(`"quality":30`) != 1 {
		t.Fatal("changed tuning not pushed")
	}
	h.send(inMsg{T: "host_preset_save", Name: "Slow"})
	h.send(inMsg{T: "host_tuning_reset"})
	if g.settings.Tuning.DriveMin != 0.8 {
		t.Fatal("reset didn't restore defaults")
	}
	if st := h.state(); len(st.Presets) != 1 || st.Presets[0] != "Slow" {
		t.Fatalf("presets %v", st.Presets)
	}
	h.send(inMsg{T: "host_preset_load", Name: "Slow"})
	if g.settings.Tuning.DriveMin != 0.6 {
		t.Fatal("preset not loaded")
	}
	h.send(inMsg{T: "host_preset_delete", Name: "Slow"})
	if st := h.state(); len(st.Presets) != 0 {
		t.Fatalf("preset not deleted: %v", st.Presets)
	}
}

// The display gets display_update snapshots and announcer events.
func TestDisplayFeed(t *testing.T) {
	g, _, _, _, _, _ := combatGame(t, nil)
	d := &testPhone{t: t, g: g, c: &client{out: make(chan []byte, 1000), closed: make(chan struct{})}}
	d.send(inMsg{T: "hello", Device: "display-1", Display: true})
	var upd displayUpdate
	_ = json.Unmarshal(<-d.c.out, &upd)
	if upd.Cmd != "display_update" || upd.Phase != "playing" || len(upd.Robots) != 2 || upd.Robots[0].VideoURL != "a" {
		t.Fatalf("display update: %+v", upd)
	}
	g.mu.Lock()
	m := g.match
	g.applyHit(m.fighters["a"], m.fighters["b"], 0x10, "S")
	g.applyHit(m.fighters["a"], m.fighters["b"], 0x10, "S")
	g.mu.Unlock()
	var texts []string
	for {
		select {
		case b := <-d.c.out:
			var e map[string]any
			_ = json.Unmarshal(b, &e)
			if e["cmd"] == "display_event" {
				texts = append(texts, e["text"].(string))
			}
			continue
		default:
		}
		break
	}
	all := strings.Join(texts, " | ")
	if !strings.Contains(all, "Rear hit! Alice hits Bob from behind!") ||
		!strings.Contains(all, "First blood — Alice eliminated Bob!") {
		t.Fatalf("announcer: %s", all)
	}
}
