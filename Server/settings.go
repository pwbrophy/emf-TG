package main

// Host-adjustable settings, saved to a JSON file so they survive restarts
// (on the router: /etc/thundergeddon/settings.json).

import (
	"encoding/json"
	"log"
	"os"
	"path/filepath"
	"strings"
)

// Settings is everything the host can change. Field names are the JSON
// names the phone pages use.
type Settings struct {
	HostPassword     string `json:"hostPassword"`
	ClientPassword   string `json:"clientPassword"`   // "" = anyone can join
	MaxPlayers       int    `json:"maxPlayers"`       // lobby size before people queue; 0 = one per connected tank
	MatchSeconds     int    `json:"matchSeconds"`     // length of a game
	CountdownSeconds int    `json:"countdownSeconds"` // "3, 2, 1, GO" before a game
	FireCooldownMs   int    `json:"fireCooldownMs"`   // minimum time between shots

	// Combat. Defaults are the Unity version's designed values (its scene).
	MaxHp               int     `json:"maxHp"`
	DamagePerHit        int     `json:"damagePerHit"`
	RearMultiplier      float64 `json:"rearMultiplier"`      // hit from behind (S)
	SideMultiplier      float64 `json:"sideMultiplier"`      // hit from the side (E or W)
	ExplosionSeconds    float64 `json:"explosionSeconds"`    // after dying: motors off, then "return to base"
	InvulnerableSeconds float64 `json:"invulnerableSeconds"` // after a respawn or base heal
	TeamPointsPerKill   int     `json:"teamPointsPerKill"`
	MaxTeamPoints       int     `json:"maxTeamPoints"` // a team reaching this wins at once

	// The IR shot handshake (see ir.go).
	IrWindowMs        int `json:"irWindowMs"`        // how long enemies listen for each barrel
	IrAckTimeoutMs    int `json:"irAckTimeoutMs"`    // wait for the shooter's "LED on"
	IrWindowTimeoutMs int `json:"irWindowTimeoutMs"` // extra wait for enemies' results

	// RFID tags (8-hex-digit UIDs) at each team's base: scanning your own base
	// respawns a dead tank or refills a live one.
	DesertBaseUids []string `json:"desertBaseUids"`
	JungleBaseUids []string `json:"jungleBaseUids"`
}

const defaultHostPassword = "thundergeddon"

func defaultSettings() Settings {
	return Settings{
		HostPassword:     defaultHostPassword,
		MatchSeconds:     5 * 60,
		CountdownSeconds: 5,
		FireCooldownMs:   3000,

		MaxHp:               100,
		DamagePerHit:        25,
		RearMultiplier:      3,
		SideMultiplier:      1.5,
		ExplosionSeconds:    5,
		InvulnerableSeconds: 5,
		TeamPointsPerKill:   25,
		MaxTeamPoints:       300,

		IrWindowMs:        100,
		IrAckTimeoutMs:    300,
		IrWindowTimeoutMs: 300,

		DesertBaseUids: []string{"118D6A5D", "F1886B01", "880409A1"},
		JungleBaseUids: []string{"11F6025D", "1168465D", "88040DA1"},
	}
}

// clamp keeps values in sensible ranges, whatever a page or an old file sent.
func (s *Settings) clamp() {
	if s.HostPassword == "" {
		s.HostPassword = defaultHostPassword
	}
	s.MaxPlayers = max(0, min(s.MaxPlayers, 40))
	s.MatchSeconds = max(30, min(s.MatchSeconds, 60*60))
	s.CountdownSeconds = max(0, min(s.CountdownSeconds, 30))
	s.FireCooldownMs = max(250, min(s.FireCooldownMs, 30000))

	s.MaxHp = max(1, min(s.MaxHp, 10000))
	s.DamagePerHit = max(0, min(s.DamagePerHit, 10000))
	s.RearMultiplier = max(0, min(s.RearMultiplier, 20))
	s.SideMultiplier = max(0, min(s.SideMultiplier, 20))
	s.ExplosionSeconds = max(0, min(s.ExplosionSeconds, 60))
	s.InvulnerableSeconds = max(0, min(s.InvulnerableSeconds, 60))
	s.TeamPointsPerKill = max(0, min(s.TeamPointsPerKill, 100000))
	s.MaxTeamPoints = max(1, min(s.MaxTeamPoints, 1000000))
	s.IrWindowMs = max(5, min(s.IrWindowMs, 1000))
	s.IrAckTimeoutMs = max(50, min(s.IrAckTimeoutMs, 3000))
	s.IrWindowTimeoutMs = max(50, min(s.IrWindowTimeoutMs, 3000))
	s.DesertBaseUids = cleanUids(s.DesertBaseUids)
	s.JungleBaseUids = cleanUids(s.JungleBaseUids)
}

// cleanUids upper-cases RFID UIDs and drops blanks and duplicates.
func cleanUids(in []string) []string {
	out := []string{}
	seen := map[string]bool{}
	for _, u := range in {
		u = strings.ToUpper(strings.TrimSpace(u))
		if u == "" || seen[u] || len(u) > 32 {
			continue
		}
		seen[u] = true
		out = append(out, u)
	}
	return out
}

func loadSettings(path string) Settings {
	s := defaultSettings()
	b, err := os.ReadFile(path)
	if err == nil {
		if err := json.Unmarshal(b, &s); err != nil {
			log.Printf("[Settings] %s is unreadable (%v); using defaults", path, err)
			s = defaultSettings()
		}
	} else if !os.IsNotExist(err) {
		log.Printf("[Settings] can't read %s: %v", path, err)
	}
	s.clamp()
	return s
}

// saveSettings writes via a temporary file and a rename, so a power cut
// mid-write can't leave a half-written settings file.
func saveSettings(path string, s Settings) {
	b, _ := json.MarshalIndent(s, "", "  ")
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		log.Printf("[Settings] can't create folder for %s: %v", path, err)
		return
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o600); err != nil {
		log.Printf("[Settings] can't write %s: %v", tmp, err)
		return
	}
	if err := os.Rename(tmp, path); err != nil {
		log.Printf("[Settings] can't replace %s: %v", path, err)
	}
}
