package main

import (
	"strings"
	"testing"
	"time"
)

// The host playing too must be back in the lobby for the next game without
// switching "play too" off and on (the bug: they stayed "finished").
func TestHostPlayerRejoinsNextGame(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01", "Jungle-02")
	h := hostUp(t, g)
	h.send(inMsg{T: "host_play", On: true, Name: "Pete"})
	h.send(inMsg{T: "pick_tank", Tank: "a"})
	h.send(inMsg{T: "host_start"})
	g.mu.Lock()
	g.phaseEndsAt = time.Now()
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	h.send(inMsg{T: "host_end"})
	h.send(inMsg{T: "host_lobby"})
	if s := h.state(); s.Me.Status != "lobby" {
		t.Fatalf("host player status %q after the game, want lobby", s.Me.Status)
	}
	h.send(inMsg{T: "pick_tank", Tank: "b"})
	if s := h.state(); s.Me.Tank != "b" {
		t.Fatal("host player couldn't choose a tank for the next game")
	}
}

// A third alliance: tanks join it by name or by the host's choice, points
// cycle through all three, and it can win.
func TestThreeAlliances(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01", "Jungle-02", "Rogue-03")
	h := hostUp(t, g)
	h.send(inMsg{T: "host_alliance_add"})
	s := h.state()
	if len(s.Alliances) != 3 {
		t.Fatalf("alliances %v", s.Alliances)
	}
	id := s.Alliances[2].ID
	h.send(inMsg{T: "host_alliance_edit", ID: id, Name: "Rogue Squad", Color: "#ff0000"})
	s = h.state()
	if s.Alliances[2].Name != "Rogue Squad" || s.Alliances[2].Color != "#ff0000" {
		t.Fatalf("edit: %+v", s.Alliances[2])
	}
	for _, tv := range s.Tanks {
		if tv.ID == "c" && tv.Alliance != "Rogue Squad" {
			t.Fatalf("Rogue-03 should join Rogue Squad by its name: %+v", tv)
		}
	}

	a := phone(t, g, "a")
	a.send(inMsg{T: "join", Name: "Alice"})
	a.send(inMsg{T: "pick_tank", Tank: "a"})
	c := phone(t, g, "c")
	c.send(inMsg{T: "join", Name: "Cat"})
	c.send(inMsg{T: "pick_tank", Tank: "c"})
	h.send(inMsg{T: "host_start"})
	g.mu.Lock()
	g.phaseEndsAt = time.Now()
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)

	h.send(inMsg{T: "host_alliance_remove", ID: id})
	if !strings.Contains(h.lastError(), "between games") {
		t.Fatal("removed an alliance mid-game")
	}
	for i := 0; i < 3; i++ {
		h.send(inMsg{T: "host_capture", Point: 0})
	}
	if s := h.state(); s.CapturePoints[0].Owner != 2 {
		t.Fatalf("three presses should give North to the third alliance, owner %d", s.CapturePoints[0].Owner)
	}
	h.send(inMsg{T: "host_capture", Point: 0})
	if s := h.state(); s.CapturePoints[0].Owner != -1 {
		t.Fatal("fourth press should free the point")
	}

	g.mu.Lock()
	g.match.alliancePoints[2] = 5
	g.endGame("time")
	g.broadcast()
	g.mu.Unlock()
	if s := h.state(); s.WinnerAlliance != "Rogue Squad" {
		t.Fatalf("winner %q", s.WinnerAlliance)
	}
}

// Tags: a new tag a tank reads is listed; once the host makes it a base it
// works as one.
func TestNewTagAssignedAsBase(t *testing.T) {
	g, _, _, h, a, _ := combatGame(t, nil)
	g.tankMessage("a", tankReport{Cmd: "rfid", UID: "deadbeef"})
	s := h.state()
	if len(s.RfidTags) == 0 || s.RfidTags[0].UID != "DEADBEEF" || s.RfidTags[0].Use != "" || s.RfidTags[0].Tank != "Desert-01" {
		t.Fatalf("new tag not listed first: %+v", s.RfidTags)
	}
	h.send(inMsg{T: "host_tag_use", UID: "DEADBEEF", Use: "base:desert"})
	g.mu.Lock()
	g.match.fighters["a"].hp = 50
	g.mu.Unlock()
	g.tankMessage("a", tankReport{Cmd: "rfid", UID: "DEADBEEF"})
	if m := a.waitFor("rfid"); m["text"] != "Hit points restored!" {
		t.Fatalf("base tag: %v", m["text"])
	}
	h.send(inMsg{T: "host_tag_forget", UID: "DEADBEEF"})
	for _, tg := range h.state().RfidTags {
		if tg.UID == "DEADBEEF" {
			t.Fatal("tag not forgotten")
		}
	}
}

// Capture points can be added, renamed, given a beacon, and removed; their
// tags are freed with them.
func TestCapturePointList(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01")
	h := hostUp(t, g)
	h.send(inMsg{T: "host_point_add"})
	s := h.state()
	p := s.CapturePoints[3]
	h.send(inMsg{T: "host_point_edit", ID: p.ID, Name: "Bridge", Beacon: 1})
	h.send(inMsg{T: "host_tag_add", UID: "abcd1234", Use: "point:" + p.ID})
	s = h.state()
	if s.CapturePoints[3].Name != "Bridge" || s.CapturePoints[3].Beacon != 1 || s.CapturePoints[3].Tags != 1 {
		t.Fatalf("edited point: %+v", s.CapturePoints[3])
	}
	if s.CapturePoints[0].Beacon != 0 {
		t.Fatal("North kept beacon 1: one beacon shows one point")
	}
	h.send(inMsg{T: "host_point_remove", ID: p.ID})
	s = h.state()
	if len(s.CapturePoints) != 3 {
		t.Fatalf("points %d", len(s.CapturePoints))
	}
	for _, tg := range s.RfidTags {
		if tg.UID == "ABCD1234" && tg.Use != "" {
			t.Fatalf("tag still points at a removed point: %+v", tg)
		}
	}
}

// Saving the general settings from the page doesn't undo list changes.
func TestSettingsSaveKeepsLists(t *testing.T) {
	g, _ := newTestGame(t, "Desert-01")
	h := hostUp(t, g)
	old := g.settings // what the page had
	h.send(inMsg{T: "host_alliance_add"})
	old.MatchSeconds = 120
	h.send(inMsg{T: "host_settings", Settings: &old})
	if len(g.settings.Alliances) != 3 || g.settings.MatchSeconds != 120 {
		t.Fatalf("alliances %d, match %d", len(g.settings.Alliances), g.settings.MatchSeconds)
	}
}

func TestLedColor(t *testing.T) {
	if r, gr, b := ledColor("#ff0000"); r != 128 || gr != 0 || b != 0 {
		t.Fatalf("red: %d %d %d", r, gr, b)
	}
	if r, gr, b := ledColor("#338c2f"); gr != 128 || r > 20 || b > 20 {
		t.Fatalf("jungle green: %d %d %d", r, gr, b)
	}
}
