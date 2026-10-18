package main

import (
	"strings"
	"testing"
	"time"
)

// The host's turret test switches the motors on while the button is held
// (tanks keep them off outside a game) and off again when it's let go.
func TestTurretTestSwitchesMotors(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01")
	h := hostUp(t, g)
	h.send(inMsg{T: "host_tank_turret", Tank: "a", S: 0.5})
	h.send(inMsg{T: "host_tank_turret", Tank: "a", S: 0})
	ft.mu.Lock()
	defer ft.mu.Unlock()
	var got []string
	for _, s := range ft.sent {
		if strings.Contains(s, "motors") || strings.Contains(s, "turret") {
			got = append(got, s)
		}
	}
	want := []string{`a {"cmd":"motors_on"}`, `a {"cmd":"turret","speed":0.500}`,
		`a {"cmd":"turret","speed":0.000}`, `a {"cmd":"motors_off"}`}
	if strings.Join(got, "|") != strings.Join(want, "|") {
		t.Fatalf("got %v\nwant %v", got, want)
	}
}

// A tank with a player glows a colour of its own in the lobby; the phone
// is told which. Colours clear when the tank is given up.
func TestLobbyTankColours(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01", "Jungle-02")
	hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})

	a.send(inMsg{T: "pick_tank", Tank: "a"})
	if s := a.state(); s.Me.TankColor != "RED" || s.Me.TankColorCSS == "" {
		t.Fatalf("Alice's tank colour %q", s.Me.TankColor)
	}
	if ft.count(`a {"cmd":"set_player_color","r":255,"g":0,"b":0}`) != 1 {
		t.Fatal("tank a not told to glow red")
	}
	b.send(inMsg{T: "pick_tank", Tank: "b"})
	if s := b.state(); s.Me.TankColor != "GREEN" {
		t.Fatalf("Bob's tank colour %q", s.Me.TankColor)
	}
	a.send(inMsg{T: "pick_tank", Tank: ""})
	if ft.count(`a {"cmd":"clear_player_color"}`) != 1 {
		t.Fatal("freed tank still glowing")
	}
	// Taking it back keeps the tank's colour.
	a.send(inMsg{T: "pick_tank", Tank: "a"})
	if s := a.state(); s.Me.TankColor != "RED" {
		t.Fatalf("Alice's tank colour %q after taking it back", s.Me.TankColor)
	}
}

// Choosing an alliance gives the player its first free tank.
func TestPickAlliance(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01", "Jungle-02", "Desert-03")
	hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})
	c := phone(t, g, "c")
	c.send(inMsg{T: "join", Name: "Cat"})

	a.send(inMsg{T: "pick_alliance", Alliance: "desert"})
	if s := a.state(); s.Me.Tank != "a" || s.Me.Alliance != "Desert Squad" {
		t.Fatalf("Alice: %+v", s.Me)
	}
	b.send(inMsg{T: "pick_alliance", Alliance: "desert"})
	if s := b.state(); s.Me.Tank != "c" {
		t.Fatalf("Bob should get Desert-03: %+v", s.Me)
	}
	c.send(inMsg{T: "pick_alliance", Alliance: "desert"})
	if !strings.Contains(c.lastError(), "No free tanks in Desert Squad") {
		t.Fatal("a full alliance should say so")
	}
	// Switching sides frees the old tank.
	a.send(inMsg{T: "pick_alliance", Alliance: "jungle"})
	if s := a.state(); s.Me.Tank != "b" {
		t.Fatalf("Alice should now drive Jungle-02: %+v", s.Me)
	}
	c.send(inMsg{T: "pick_alliance", Alliance: "desert"})
	if s := c.state(); s.Me.Tank != "a" {
		t.Fatalf("Cat should get the freed Desert-01: %+v", s.Me)
	}
}

// Cameras stream only for tanks in the game, from the countdown to game
// over, and not while paused.
func TestCamerasOnlyDuringGames(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01", "Jungle-02")
	for _, id := range []string{"a", "b"} {
		g.tankConnected(id)
	}
	h := hostUp(t, g)
	if ft.cameraOn("a") || ft.cameraOn("b") {
		t.Fatal("cameras on in the lobby")
	}
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	a.send(inMsg{T: "pick_tank", Tank: "a"})
	h.send(inMsg{T: "host_start"})
	if !ft.cameraOn("a") || ft.cameraOn("b") {
		t.Fatalf("countdown: a on %v (want true), b on %v (want false)", ft.cameraOn("a"), ft.cameraOn("b"))
	}
	g.mu.Lock()
	g.phaseEndsAt = time.Now()
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	h.send(inMsg{T: "host_pause", On: true})
	if ft.cameraOn("a") {
		t.Fatal("camera on while paused")
	}
	h.send(inMsg{T: "host_pause", On: false})
	if !ft.cameraOn("a") {
		t.Fatal("camera off after resuming")
	}
	h.send(inMsg{T: "host_end"})
	if ft.cameraOn("a") {
		t.Fatal("camera on after the game")
	}

	s := g.settings
	s.CamerasAlwaysOn = true
	h.send(inMsg{T: "host_settings", Settings: &s})
	if !ft.cameraOn("a") || !ft.cameraOn("b") {
		t.Fatal("'keep cameras on' didn't switch them on")
	}
}

// The host's compass gets the last hit's sensors and side.
func TestHitCompassData(t *testing.T) {
	g, _, _, h, _, _ := combatGame(t, nil)
	h.send(inMsg{T: "host_damage", Tank: "b", Dir: "E"})
	for _, tv := range h.state().Tanks {
		if tv.ID == "b" && (tv.LastHitDir != "E" || tv.LastHitMask != 1<<2 || tv.Hits != 1) {
			t.Fatalf("compass data: %+v", tv)
		}
	}
	_ = g
}
