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

	// Capture points: the RFID tags at North, Centre and South. A team earns
	// one team point every CaptureSeconds divided by the points it holds.
	NorthPointUids  []string `json:"northPointUids"`
	CentrePointUids []string `json:"centrePointUids"`
	SouthPointUids  []string `json:"southPointUids"`
	CaptureSeconds  float64  `json:"captureSeconds"`

	// Teams: names shown everywhere, and tanks moved to a team other than the
	// one their name says (Desert-… / Jungle-…). Tank id → 0 Desert, 1 Jungle.
	TeamNames [2]string      `json:"teamNames"`
	TankTeams map[string]int `json:"tankTeams"`

	TwoPlayerTanks bool `json:"twoPlayerTanks"` // a second player can join a tank as gunner
	DebugConsole   bool `json:"debugConsole"`   // the "DBG" log button on players' phones

	Tuning Tuning `json:"tuning"` // Advanced: how tanks and controls behave
}

// Tuning is the Unity version's "Advanced" settings: motor behaviour and
// camera settings sent to every tank (when it connects and when changed),
// and the joystick response sent to the phones. Defaults are the firmware's
// own defaults and the video settings the tanks have saved, so nothing
// changes until the host changes it.
type Tuning struct {
	DriveAccel       float64 `json:"driveAccel"`
	DriveDecel       float64 `json:"driveDecel"`
	DriveMin         float64 `json:"driveMin"` // below this the tracks pulse
	DrivePulseMs     int     `json:"drivePulseMs"`
	DriveKickMs      int     `json:"driveKickMs"`
	DriveBrake       bool    `json:"driveBrake"`
	DriveBacklashMs  int     `json:"driveBacklashMs"`
	TurretAccel      float64 `json:"turretAccel"`
	TurretDecel      float64 `json:"turretDecel"`
	TurretMin        float64 `json:"turretMin"`
	TurretPulseMs    int     `json:"turretPulseMs"`
	TurretKickMs     int     `json:"turretKickMs"`
	TurretBrake      bool    `json:"turretBrake"`
	TurretBacklashMs int     `json:"turretBacklashMs"`

	SlowTurretSpeed   float64 `json:"slowTurretSpeed"`   // the turret slider's SLOW zones
	SlowTurretEnabled bool    `json:"slowTurretEnabled"` // cap every turret speed at the slow speed

	JoyDeadzone   float64 `json:"joyDeadzone"`
	JoyStartSpeed float64 `json:"joyStartSpeed"`
	JoyFullAt     float64 `json:"joyFullAt"`
	JoyCurve      float64 `json:"joyCurve"`
	JoyAxisSnap   float64 `json:"joyAxisSnap"`
	JoyTurnSpeed  float64 `json:"joyTurnSpeed"`

	Buzzer         bool `json:"buzzer"`
	VideoFps       int  `json:"videoFps"`
	VideoFrameSize int  `json:"videoFrameSize"` // the camera's frame size index
	VideoQuality   int  `json:"videoQuality"`   // JPEG quality: lower = better picture, bigger frames
}

const defaultHostPassword = "thundergeddon"

func defaultTuning() Tuning {
	return Tuning{
		DriveMin: 0.8, DrivePulseMs: 20, DriveKickMs: 120, DriveBrake: true,
		TurretMin: 0.4, TurretPulseMs: 20, TurretKickMs: 20, TurretBrake: true, TurretBacklashMs: 110,
		SlowTurretSpeed: 0.2,
		JoyDeadzone:     0.08, JoyStartSpeed: 0.4, JoyFullAt: 1, JoyCurve: 2, JoyAxisSnap: 0.15, JoyTurnSpeed: 0.7,
		Buzzer:          true, VideoFps: 25, VideoFrameSize: 2, VideoQuality: 20,
	}
}

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

		NorthPointUids:  []string{"110E8C5D", "11F79E5D", "88040CA1"},
		CentrePointUids: []string{"11E0A45D", "1186AE5D", "88040BA1"},
		SouthPointUids:  []string{"114DBB5D", "01DA2C5D", "88040AA1"},
		CaptureSeconds:  3,

		TeamNames: [2]string{"Desert Squad", "Jungle Squad"},
		Tuning:    defaultTuning(),
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
	s.NorthPointUids = cleanUids(s.NorthPointUids)
	s.CentrePointUids = cleanUids(s.CentrePointUids)
	s.SouthPointUids = cleanUids(s.SouthPointUids)
	s.CaptureSeconds = max(0.5, min(s.CaptureSeconds, 600))

	def := defaultSettings().TeamNames
	for i := range s.TeamNames {
		s.TeamNames[i] = strings.TrimSpace(s.TeamNames[i])
		if s.TeamNames[i] == "" || len(s.TeamNames[i]) > 24 {
			s.TeamNames[i] = def[i]
		}
	}
	for id, t := range s.TankTeams {
		if t != 0 && t != 1 {
			delete(s.TankTeams, id)
		}
	}

	t := &s.Tuning
	unit := func(v float64) float64 { return max(0, min(v, 1)) }
	ms := func(v, hi int) int { return max(0, min(v, hi)) }
	t.DriveAccel, t.DriveDecel = max(0, min(t.DriveAccel, 100)), max(0, min(t.DriveDecel, 100))
	t.TurretAccel, t.TurretDecel = max(0, min(t.TurretAccel, 100)), max(0, min(t.TurretDecel, 100))
	t.DriveMin, t.TurretMin = unit(t.DriveMin), unit(t.TurretMin)
	t.DrivePulseMs, t.TurretPulseMs = max(10, min(t.DrivePulseMs, 1000)), max(10, min(t.TurretPulseMs, 1000))
	t.DriveKickMs, t.TurretKickMs = ms(t.DriveKickMs, 200), ms(t.TurretKickMs, 200)
	t.DriveBacklashMs, t.TurretBacklashMs = ms(t.DriveBacklashMs, 300), ms(t.TurretBacklashMs, 300)
	t.SlowTurretSpeed = unit(t.SlowTurretSpeed)
	t.JoyDeadzone = max(0, min(t.JoyDeadzone, 0.5))
	t.JoyStartSpeed = unit(t.JoyStartSpeed)
	t.JoyFullAt = max(t.JoyDeadzone+0.05, min(t.JoyFullAt, 1))
	t.JoyCurve = max(0.2, min(t.JoyCurve, 5))
	t.JoyAxisSnap = max(0, min(t.JoyAxisSnap, 0.5))
	t.JoyTurnSpeed = max(0.05, min(t.JoyTurnSpeed, 1))
	t.VideoFps = max(1, min(t.VideoFps, 30))
	t.VideoFrameSize = max(0, min(t.VideoFrameSize, 13))
	t.VideoQuality = max(4, min(t.VideoQuality, 63))
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
