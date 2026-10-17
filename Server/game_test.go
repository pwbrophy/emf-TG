package main

import (
	"encoding/json"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

// fakeTanks records the commands the game sends to tanks.
type fakeTanks struct {
	mu   sync.Mutex
	ts   []tankInfo
	sent []string // "id cmd-json"
}

func (f *fakeTanks) list() []tankInfo {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]tankInfo(nil), f.ts...)
}

func (f *fakeTanks) sendTo(id, msg string) bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.sent = append(f.sent, id+" "+msg)
	return true
}

func (f *fakeTanks) count(substr string) int {
	f.mu.Lock()
	defer f.mu.Unlock()
	n := 0
	for _, s := range f.sent {
		if strings.Contains(s, substr) {
			n++
		}
	}
	return n
}

type testPhone struct {
	t *testing.T
	g *game
	c *client
}

func newTestGame(t *testing.T, tanks ...string) (*game, *fakeTanks) {
	ft := &fakeTanks{}
	for i, name := range tanks {
		ft.ts = append(ft.ts, tankInfo{ID: string(rune('a' + i)), Name: name})
	}
	g := newGame(ft, filepath.Join(t.TempDir(), "settings.json"))
	g.settings.CountdownSeconds = 1
	return g, ft
}

func phone(t *testing.T, g *game, device string) *testPhone {
	p := &testPhone{t: t, g: g, c: &client{out: make(chan []byte, 1000), closed: make(chan struct{})}}
	p.send(inMsg{T: "hello", Device: device})
	return p
}

func (p *testPhone) send(m inMsg) { p.g.handle(p.c, m) }

// last returns the newest state snapshot, and any other messages since.
func (p *testPhone) last() (snapshot, []map[string]any) {
	var st snapshot
	var others []map[string]any
	for {
		select {
		case b := <-p.c.out:
			var head struct{ T string }
			_ = json.Unmarshal(b, &head)
			if head.T == "state" {
				st = snapshot{}
				_ = json.Unmarshal(b, &st)
			} else {
				var m map[string]any
				_ = json.Unmarshal(b, &m)
				others = append(others, m)
			}
		default:
			return st, others
		}
	}
}

func (p *testPhone) state() snapshot { s, _ := p.last(); return s }

func (p *testPhone) lastError() string {
	_, others := p.last()
	for i := len(others) - 1; i >= 0; i-- {
		if others[i]["t"] == "error" {
			return others[i]["msg"].(string)
		}
	}
	return ""
}

func hostUp(t *testing.T, g *game) *testPhone {
	h := phone(t, g, "host")
	h.send(inMsg{T: "host_login", Password: defaultHostPassword})
	h.send(inMsg{T: "host_open"})
	if s := h.state(); !s.Me.IsHost || s.Phase != phaseLobby {
		t.Fatalf("host not in an open lobby: %+v", s)
	}
	return h
}

func TestQueueBeforeHostThenLobbyByCapacity(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01", "Jungle-02")
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	if s := a.state(); s.Me.Status != "queued" || s.Me.QueuePos != 1 || s.Hosted {
		t.Fatalf("before a host, Alice should be queued first: %+v", s.Me)
	}
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})
	c := phone(t, g, "c")
	c.send(inMsg{T: "join", Name: "Cat"})

	hostUp(t, g) // 2 tanks → lobby holds 2
	if s := a.state(); s.Me.Status != "lobby" {
		t.Errorf("Alice: %s, want lobby", s.Me.Status)
	}
	if s := b.state(); s.Me.Status != "lobby" {
		t.Errorf("Bob: %s, want lobby", s.Me.Status)
	}
	if s := c.state(); s.Me.Status != "queued" || s.Me.QueuePos != 1 {
		t.Errorf("Cat: %s pos %d, want queued 1", s.Me.Status, s.Me.QueuePos)
	}
}

func TestWrongPasswords(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01")
	h := phone(t, g, "host")
	h.send(inMsg{T: "host_login", Password: "nope"})
	if !strings.Contains(h.lastError(), "Wrong host password") || h.g.hostDevice != "" {
		t.Fatal("wrong host password accepted")
	}
	h.send(inMsg{T: "host_open"})
	if g.phase != phaseIdle {
		t.Fatal("non-host opened the lobby")
	}

	h.send(inMsg{T: "host_login", Password: defaultHostPassword})
	st := g.settings
	st.ClientPassword = "tanks"
	h.send(inMsg{T: "host_settings", Settings: &st})

	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice", Password: "x"})
	if !strings.Contains(a.lastError(), "Wrong game password") {
		t.Fatal("wrong game password accepted")
	}
	a.send(inMsg{T: "join", Name: "Alice", Password: "tanks"})
	if a.state().Me.Status == "" {
		t.Fatal("right game password refused")
	}
}

func TestPlayerCantUseHostActions(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01")
	hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	a.send(inMsg{T: "host_close"})
	if g.phase != phaseLobby || !strings.Contains(a.lastError(), "Only the host") {
		t.Fatal("a player could close the game")
	}
}

func TestTankChoice(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01", "Jungle-02")
	hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})

	a.send(inMsg{T: "pick_tank", Tank: "a"})
	b.send(inMsg{T: "pick_tank", Tank: "a"})
	if !strings.Contains(b.lastError(), "Alice already has that tank") {
		t.Fatal("Bob took Alice's tank")
	}
	b.send(inMsg{T: "pick_tank", Tank: "zzz"})
	if !strings.Contains(b.lastError(), "isn't connected") {
		t.Fatal("picked a tank that doesn't exist")
	}
	b.send(inMsg{T: "pick_tank", Tank: "b"})
	s := b.state()
	if s.Me.Tank != "b" || s.Me.TankName != "Jungle-02" || s.Me.Team != "Jungle Squad" {
		t.Fatalf("Bob's tank: %+v", s.Me)
	}
}

func TestFullGameAndBackOfQueue(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01")
	h := hostUp(t, g) // 1 tank → lobby of 1
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"}) // queued behind Alice

	a.send(inMsg{T: "pick_tank", Tank: "a"})
	h.send(inMsg{T: "host_start"})
	if g.phase != phaseCountdown {
		t.Fatalf("phase %s, want countdown", g.phase)
	}

	// Driving before GO does nothing.
	a.send(inMsg{T: "drive", L: 1, R: 1})
	if ft.count(`"drive","l":1.000`) != 0 {
		t.Fatal("drive went through during the countdown")
	}

	g.phaseEndsAt = time.Now() // skip the countdown
	g.tick()
	if g.phase != phasePlaying || ft.count("a "+`{"cmd":"motors_on"}`) != 1 {
		t.Fatalf("phase %s, motors_on sent %d times", g.phase, ft.count("motors_on"))
	}

	a.send(inMsg{T: "drive", L: 1, R: 1})
	b.send(inMsg{T: "drive", L: 1, R: 1}) // Bob isn't playing
	if n := ft.count(`"drive","l":1.000`); n != 1 {
		t.Fatalf("drive commands sent: %d, want 1 (Alice only)", n)
	}

	a.send(inMsg{T: "fire"})
	a.send(inMsg{T: "fire"}) // inside the cooldown
	if n := ft.count("flash_fire"); n != 1 {
		t.Fatalf("shots sent: %d, want 1", n)
	}
	fired := 0
	_, others := a.last()
	for _, m := range others {
		if m["t"] == "fired" {
			fired++
		}
	}
	if fired != 1 {
		t.Fatalf("shooter told it fired %d times, want 1", fired)
	}

	g.phaseEndsAt = time.Now() // time's up
	g.tick()
	sa := a.state()
	if g.phase != phaseEnded || sa.Me.Status != "finished" || len(sa.Results) != 1 || sa.Results[0].Shots != 1 {
		t.Fatalf("after the game: phase %s, Alice %+v, results %+v", g.phase, sa.Me, sa.Results)
	}
	if ft.count("a "+`{"cmd":"motors_off"}`) != 1 {
		t.Fatal("tank not stopped at game over")
	}

	// Alice plays again: she goes behind Bob, who waited through the game.
	a.send(inMsg{T: "play_again"})
	if s := a.state(); s.Me.Status != "queued" || s.Me.QueuePos != 2 {
		t.Fatalf("Alice after play again: %+v", s.Me)
	}
	h.send(inMsg{T: "host_lobby"})
	if s := b.state(); s.Me.Status != "lobby" {
		t.Fatalf("Bob should be in the lobby now: %+v", s.Me)
	}
	if s := a.state(); s.Me.Status != "queued" || s.Me.QueuePos != 1 {
		t.Fatalf("Alice should be next in line: %+v", s.Me)
	}
}

func TestStartWarnsAboutPlayersWithoutTanks(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01", "Jungle-02")
	h := hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})
	a.send(inMsg{T: "pick_tank", Tank: "a"})

	h.send(inMsg{T: "host_start"})
	_, others := h.last()
	if g.phase != phaseLobby || len(others) == 0 || others[len(others)-1]["t"] != "confirm_start" {
		t.Fatalf("expected a confirm_start warning, phase %s, got %v", g.phase, others)
	}
	h.send(inMsg{T: "host_start", Force: true})
	if g.phase != phaseCountdown {
		t.Fatal("forced start didn't start")
	}
	if s := b.state(); s.Me.Status != "lobby" {
		t.Fatalf("Bob (no tank) should wait in the lobby: %s", s.Me.Status)
	}
}

func TestDisconnectedDriverStopsTank(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01")
	h := hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	a.send(inMsg{T: "pick_tank", Tank: "a"})
	h.send(inMsg{T: "host_start"})
	g.phaseEndsAt = time.Now()
	g.tick()

	g.clientGone(a.c)
	if ft.count(`a {"cmd":"drive","l":0,"r":0}`) != 1 {
		t.Fatal("tank not stopped when its driver's page closed")
	}
	// Reopening the page picks the game back up.
	a2 := phone(t, g, "a")
	if s := a2.state(); s.Me.Status != "playing" || s.Me.Tank != "a" {
		t.Fatalf("rejoin: %+v", s.Me)
	}
}

func TestKickAndCloseGame(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01", "Jungle-02")
	h := hostUp(t, g)
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "Bob"})

	h.send(inMsg{T: "host_kick", Target: "a"})
	sa, others := a.last()
	if sa.Me.Status != "" || len(others) == 0 || others[0]["t"] != "kicked" {
		t.Fatalf("kick: %+v %v", sa.Me, others)
	}

	h.send(inMsg{T: "host_close"})
	if s := b.state(); s.Hosted || s.Me.Status != "queued" || s.Me.QueuePos != 1 {
		t.Fatalf("after close, Bob should wait first in line: hosted=%v %+v", s.Hosted, s.Me)
	}
}

func TestUniqueNames(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01")
	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Sam"})
	b := phone(t, g, "b")
	b.send(inMsg{T: "join", Name: "sam"})
	if n := b.state().Me.Name; n != "sam 2" {
		t.Fatalf("second Sam named %q", n)
	}
}
