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
	ClientPassword   string `json:"clientPassword"`   // players enter it on Name Entry; "" = anyone can join
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
	PointsPerKill       int     `json:"pointsPerKill"`
	PointsToWin         int     `json:"pointsToWin"` // an alliance reaching this wins at once

	// The IR shot handshake (see ir.go).
	IrWindowMs        int `json:"irWindowMs"`        // how long enemies listen for each barrel
	IrAckTimeoutMs    int `json:"irAckTimeoutMs"`    // wait for the shooter's "LED on"
	IrWindowTimeoutMs int `json:"irWindowTimeoutMs"` // extra wait for enemies' results

	// Alliances: the sides in a game. A tank is in the alliance its name
	// starts with (Desert-01 → "desert") unless the host chose another one
	// for it (TankAlliances: tank id → alliance id).
	Alliances     []Alliance        `json:"alliances"`
	TankAlliances map[string]string `json:"tankAlliances"`

	// Capture points. An alliance earns one point every CaptureSeconds
	// divided by the number of points it holds.
	CapturePoints  []CapturePoint `json:"capturePoints"`
	CaptureSeconds float64        `json:"captureSeconds"`

	// Every RFID tag the hub knows: ones the host assigned to a base or a
	// capture point, and ones a tank has scanned (so the host can assign them).
	RfidTags []RfidTag `json:"rfidTags"`

	TwoPlayerTanks bool `json:"twoPlayerTanks"` // a second player can join a tank as gunner

	// Tank cameras normally stream only while their tank is in a game. On =
	// every camera streams all the time (for the video test page and load
	// tests).
	CamerasAlwaysOn bool `json:"camerasAlwaysOn"`
	DebugConsole    bool `json:"debugConsole"` // the "DBG" log button on players' phones

	AirtimeFairness bool `json:"airtimeFairness"` // the router's airtime fairness, on both Wi-Fi bands; off by default (airtime.go)

	Tuning Tuning `json:"tuning"` // Advanced: how tanks and controls behave

	SignalTest SignalTestSettings `json:"signalTest"` // Server Config → Signal Quality Test (signaltest.go)

	// Realtime or turn-based (turns.go). The realtime game's rules are the
	// fields above; the turn-based game has its own copies in Turns, so
	// changing one game never changes the other.
	GameMode string       `json:"gameMode"` // modeRealtime or modeTurns
	Turns    TurnSettings `json:"turns"`
}

const (
	modeRealtime = "realtime"
	modeTurns    = "turns"
)

// TurnSettings are the turn-based game's rules (turns.go).
type TurnSettings struct {
	// The game rules the realtime game has too, with separate values.
	MaxHp            int     `json:"maxHp"`
	DamagePerHit     int     `json:"damagePerHit"`
	RearMultiplier   float64 `json:"rearMultiplier"`
	SideMultiplier   float64 `json:"sideMultiplier"`
	FireCooldownMs   int     `json:"fireCooldownMs"`
	PointsPerKill    int     `json:"pointsPerKill"`
	PointsToWin      int     `json:"pointsToWin"`
	CountdownSeconds int     `json:"countdownSeconds"`

	// Action points: every tank gets ApPerTurn at the start of its
	// alliance's turn. Driving costs DriveApPerSec per second with both
	// tracks at full speed (one track: half), the turret TurretApPerSec, a
	// shot FireAp.
	ApPerTurn      float64 `json:"apPerTurn"`
	DriveApPerSec  float64 `json:"driveApPerSec"`
	TurretApPerSec float64 `json:"turretApPerSec"`
	FireAp         float64 `json:"fireAp"`
	ShotWindowMs   int     `json:"shotWindowMs"` // how long enemies listen for each barrel of a shot

	// Overwatch: a tank keeps its AP and fires once, by itself, at the
	// first enemy that moves into its line of fire during an enemy turn.
	Overwatch         bool `json:"overwatch"`
	OverwatchWindowMs int  `json:"overwatchWindowMs"` // the probes' listen window: short, so they can run back to back

	// Shield recharge: once per turn a tank can spend ShieldRechargeAp to
	// get back ShieldRechargePct % of its max HP (0 % = switched off).
	ShieldRechargePct float64 `json:"shieldRechargePct"`
	ShieldRechargeAp  float64 `json:"shieldRechargeAp"`

	TurnSeconds          int `json:"turnSeconds"`          // 0 = no time limit
	HandoverSeconds      int `json:"handoverSeconds"`      // pause between turns
	MaxRounds            int `json:"maxRounds"`            // 0 = no limit
	CapturePointsPerTurn int `json:"capturePointsPerTurn"` // per point held, at the start of the alliance's turn
	MaxTanksPerArmy      int `json:"maxTanksPerArmy"`      // 0 = no limit
}

func defaultTurnSettings() TurnSettings {
	return TurnSettings{
		MaxHp: 100, DamagePerHit: 25, RearMultiplier: 3, SideMultiplier: 1.5, FireCooldownMs: 1000,
		PointsPerKill: 25, PointsToWin: 300, CountdownSeconds: 5,
		ApPerTurn: 10, DriveApPerSec: 1, TurretApPerSec: 0, FireAp: 4, ShotWindowMs: 250,
		Overwatch: true, OverwatchWindowMs: 40,
		ShieldRechargePct: 20, ShieldRechargeAp: 5,
		TurnSeconds: 90, HandoverSeconds: 3, CapturePointsPerTurn: 10,
	}
}

func (t *TurnSettings) clamp() {
	t.MaxHp = max(1, min(t.MaxHp, 10000))
	t.DamagePerHit = max(0, min(t.DamagePerHit, 10000))
	t.RearMultiplier = max(0, min(t.RearMultiplier, 20))
	t.SideMultiplier = max(0, min(t.SideMultiplier, 20))
	t.FireCooldownMs = max(250, min(t.FireCooldownMs, 30000))
	t.PointsPerKill = max(0, min(t.PointsPerKill, 100000))
	t.PointsToWin = max(1, min(t.PointsToWin, 1000000))
	t.CountdownSeconds = max(0, min(t.CountdownSeconds, 30))
	t.ApPerTurn = max(0.5, min(t.ApPerTurn, 1000))
	t.DriveApPerSec = max(0, min(t.DriveApPerSec, 100))
	t.TurretApPerSec = max(0, min(t.TurretApPerSec, 100))
	t.FireAp = max(0, min(t.FireAp, 1000))
	t.ShotWindowMs = max(5, min(t.ShotWindowMs, 1000))
	t.OverwatchWindowMs = max(5, min(t.OverwatchWindowMs, 1000))
	t.ShieldRechargePct = max(0, min(t.ShieldRechargePct, 100))
	t.ShieldRechargeAp = max(0, min(t.ShieldRechargeAp, 1000))
	t.TurnSeconds = max(0, min(t.TurnSeconds, 60*60))
	t.HandoverSeconds = max(0, min(t.HandoverSeconds, 30))
	t.MaxRounds = max(0, min(t.MaxRounds, 1000))
	t.CapturePointsPerTurn = max(0, min(t.CapturePointsPerTurn, 100000))
	t.MaxTanksPerArmy = max(0, min(t.MaxTanksPerArmy, 40))
}

// Rules are the game rules both games have, read through game.rules() so
// each game uses its own values.
type Rules struct {
	MaxHp            int
	DamagePerHit     int
	RearMultiplier   float64
	SideMultiplier   float64
	FireCooldownMs   int
	PointsPerKill    int
	PointsToWin      int
	CountdownSeconds int
}

func (s *Settings) rules(turns bool) Rules {
	if turns {
		t := s.Turns
		return Rules{t.MaxHp, t.DamagePerHit, t.RearMultiplier, t.SideMultiplier, t.FireCooldownMs,
			t.PointsPerKill, t.PointsToWin, t.CountdownSeconds}
	}
	return Rules{s.MaxHp, s.DamagePerHit, s.RearMultiplier, s.SideMultiplier, s.FireCooldownMs,
		s.PointsPerKill, s.PointsToWin, s.CountdownSeconds}
}

// Alliance is one side in a game. In the turn-based game an alliance holds
// several armies (one player each, with several tanks); in realtime each
// player has one tank.
type Alliance struct {
	ID    string `json:"id"`    // stable: tank names and tags refer to it
	Name  string `json:"name"`  // "Desert Squad"
	Color string `json:"color"` // "#rrggbb", used on screens and the beacons
}

// CapturePoint is a place tanks capture by scanning its tag.
type CapturePoint struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	Beacon int    `json:"beacon"` // the LED beacon that shows it: 1-3 (North, Centre, South beacons), 0 = none
}

// RfidTag is one tag and what it is for: "base:<alliance id>",
// "point:<capture point id>", or "" (not used).
type RfidTag struct {
	UID string `json:"uid"`
	Use string `json:"use"`
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

	// Turret stabilisation (stabilise.go). The hull's turn rate is worked out
	// from the drive commands, so these describe how the hull really turns.
	StabAllowed    bool    `json:"stabAllowed"`    // players get the STAB button
	StabGain       float64 `json:"stabGain"`       // turret speed per unit of hull turn ((l-r)/2), turning on the spot
	StabArcGain    float64 `json:"stabArcGain"`    // × StabGain when turning while driving forward or back
	StabCurve      float64 `json:"stabCurve"`      // 1 = in proportion; above 1 = gentle turns get less correction
	StabDeadband   float64 `json:"stabDeadband"`   // hull turns smaller than this get no correction
	StabMax        float64 `json:"stabMax"`        // the most correction added (at or below TurretMin keeps it pulsed)
	StabSpinUpMs   int     `json:"stabSpinUpMs"`   // how long the hull takes to get up to its turning speed
	StabSpinDownMs int     `json:"stabSpinDownMs"` // how long it takes to stop turning
	StabSendMs     int     `json:"stabSendMs"`     // shortest gap between turret updates while the correction changes
	StabInvert     bool    `json:"stabInvert"`     // the turret corrects the wrong way: flip it

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

func defaultTuning() Tuning {
	return Tuning{
		DriveMin: 0.8, DrivePulseMs: 20, DriveKickMs: 120, DriveBrake: true,
		TurretMin: 0.4, TurretPulseMs: 20, TurretKickMs: 20, TurretBrake: true, TurretBacklashMs: 110,
		SlowTurretSpeed: 0.2,
		StabAllowed:     true, StabGain: 0.5, StabArcGain: 1, StabCurve: 1, StabDeadband: 0.05, StabMax: 0.4,
		StabSpinUpMs: 150, StabSpinDownMs: 100, StabSendMs: 50,
		JoyDeadzone: 0.08, JoyStartSpeed: 0.4, JoyFullAt: 1, JoyCurve: 2, JoyAxisSnap: 0.15, JoyTurnSpeed: 0.7,
		Buzzer: true, VideoFps: 25, VideoFrameSize: 2, VideoQuality: 20,
	}
}

func defaultSettings() Settings {
	s := Settings{
		MatchSeconds:     5 * 60,
		CountdownSeconds: 5,
		FireCooldownMs:   3000,

		MaxHp:               100,
		DamagePerHit:        25,
		RearMultiplier:      3,
		SideMultiplier:      1.5,
		ExplosionSeconds:    5,
		InvulnerableSeconds: 5,
		PointsPerKill:       25,
		PointsToWin:         300,

		IrWindowMs:        100,
		IrAckTimeoutMs:    300,
		IrWindowTimeoutMs: 300,

		// The colours the Unity-era spectator display used.
		Alliances: []Alliance{
			{ID: "desert", Name: "Desert Squad", Color: "#ffd594"},
			{ID: "jungle", Name: "Jungle Squad", Color: "#338c2f"},
		},
		CapturePoints: []CapturePoint{
			{ID: "north", Name: "North", Beacon: 1},
			{ID: "centre", Name: "Centre", Beacon: 2},
			{ID: "south", Name: "South", Beacon: 3},
		},
		CaptureSeconds: 3,

		DebugConsole: true,
		Tuning:       defaultTuning(),
		SignalTest:   defaultSignalTest(),
		GameMode:     modeRealtime,
		Turns:        defaultTurnSettings(),
	}
	// The tags in the Unity scene.
	add := func(use string, uids ...string) {
		for _, u := range uids {
			s.RfidTags = append(s.RfidTags, RfidTag{UID: u, Use: use})
		}
	}
	add("base:desert", "118D6A5D", "F1886B01", "880409A1")
	add("base:jungle", "11F6025D", "1168465D", "88040DA1")
	add("point:north", "110E8C5D", "11F79E5D", "88040CA1")
	add("point:centre", "11E0A45D", "1186AE5D", "88040BA1")
	add("point:south", "114DBB5D", "01DA2C5D", "88040AA1")
	return s
}

// clamp keeps values in sensible ranges, whatever a page or an old file sent.
func (s *Settings) clamp() {
	s.ClientPassword = strings.TrimSpace(s.ClientPassword)
	if len(s.ClientPassword) > 32 {
		s.ClientPassword = s.ClientPassword[:32]
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
	s.PointsPerKill = max(0, min(s.PointsPerKill, 100000))
	s.PointsToWin = max(1, min(s.PointsToWin, 1000000))
	s.IrWindowMs = max(5, min(s.IrWindowMs, 1000))
	s.IrAckTimeoutMs = max(50, min(s.IrAckTimeoutMs, 3000))
	s.IrWindowTimeoutMs = max(50, min(s.IrWindowTimeoutMs, 3000))
	s.CaptureSeconds = max(0.5, min(s.CaptureSeconds, 600))
	s.clampAlliances()
	s.clampPoints()
	s.clampTags()
	s.SignalTest.clamp()
	if s.GameMode != modeTurns {
		s.GameMode = modeRealtime
	}
	s.Turns.clamp()

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
	t.StabGain, t.StabArcGain = max(0, min(t.StabGain, 3)), max(0, min(t.StabArcGain, 3))
	t.StabCurve = max(0.2, min(t.StabCurve, 5))
	t.StabDeadband = max(0, min(t.StabDeadband, 0.5))
	t.StabMax = unit(t.StabMax)
	t.StabSpinUpMs, t.StabSpinDownMs = ms(t.StabSpinUpMs, 2000), ms(t.StabSpinDownMs, 2000)
	t.StabSendMs = max(20, min(t.StabSendMs, 500))
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

const (
	maxAlliances     = 8
	maxCapturePoints = 12
	maxRfidTags      = 200
)

func (s *Settings) clampAlliances() {
	var out []Alliance
	seen := map[string]bool{}
	for _, a := range s.Alliances {
		a.ID = cleanID(a.ID)
		a.Name = strings.TrimSpace(a.Name)
		if a.ID == "" || seen[a.ID] || len(out) == maxAlliances {
			continue
		}
		seen[a.ID] = true
		if a.Name == "" || len(a.Name) > 24 {
			a.Name = a.ID
		}
		a.Color = cleanColor(a.Color)
		out = append(out, a)
	}
	if len(out) == 0 {
		out = defaultSettings().Alliances
	}
	s.Alliances = out
	for tank, id := range s.TankAlliances {
		if !seen[id] {
			delete(s.TankAlliances, tank)
		}
	}
}

func (s *Settings) clampPoints() {
	var out []CapturePoint
	seen := map[string]bool{}
	for _, p := range s.CapturePoints {
		p.ID = cleanID(p.ID)
		p.Name = strings.TrimSpace(p.Name)
		if p.ID == "" || seen[p.ID] || len(out) == maxCapturePoints {
			continue
		}
		seen[p.ID] = true
		if p.Name == "" || len(p.Name) > 24 {
			p.Name = p.ID
		}
		if p.Beacon < 0 || p.Beacon > 3 {
			p.Beacon = 0
		}
		out = append(out, p)
	}
	s.CapturePoints = out
}

// clampTags upper-cases UIDs, drops duplicates, and forgets uses that point
// at an alliance or capture point that no longer exists.
func (s *Settings) clampTags() {
	alliances, points := map[string]bool{}, map[string]bool{}
	for _, a := range s.Alliances {
		alliances[a.ID] = true
	}
	for _, p := range s.CapturePoints {
		points[p.ID] = true
	}
	var out []RfidTag
	seen := map[string]bool{}
	for _, t := range s.RfidTags {
		t.UID = strings.ToUpper(strings.TrimSpace(t.UID))
		if t.UID == "" || len(t.UID) > 32 || seen[t.UID] || len(out) == maxRfidTags {
			continue
		}
		seen[t.UID] = true
		kind, id, _ := strings.Cut(t.Use, ":")
		if !(kind == "base" && alliances[id]) && !(kind == "point" && points[id]) {
			t.Use = ""
		}
		out = append(out, t)
	}
	s.RfidTags = out
}

// cleanID keeps lower-case letters, digits and dashes.
func cleanID(id string) string {
	var b strings.Builder
	for _, r := range strings.ToLower(strings.TrimSpace(id)) {
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '-' {
			b.WriteRune(r)
		}
		if b.Len() == 24 {
			break
		}
	}
	return b.String()
}

// cleanColor accepts "#rrggbb"; anything else becomes grey.
func cleanColor(c string) string {
	c = strings.ToLower(strings.TrimSpace(c))
	if len(c) != 7 || c[0] != '#' {
		return "#888888"
	}
	for _, r := range c[1:] {
		if !((r >= '0' && r <= '9') || (r >= 'a' && r <= 'f')) {
			return "#888888"
		}
	}
	return c
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
