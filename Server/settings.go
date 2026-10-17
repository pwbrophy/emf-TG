package main

// Host-adjustable settings, saved to a JSON file so they survive restarts
// (on the router: /etc/thundergeddon/settings.json).

import (
	"encoding/json"
	"log"
	"os"
	"path/filepath"
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
}

const defaultHostPassword = "thundergeddon"

func defaultSettings() Settings {
	return Settings{
		HostPassword:     defaultHostPassword,
		MatchSeconds:     5 * 60,
		CountdownSeconds: 5,
		FireCooldownMs:   3000,
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
