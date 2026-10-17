package main

// The host's tools: pause, test hits, heal, capture points by hand, each
// tank's flips / reversed controls / name / team, a ping test, the turret
// test, and the Advanced tuning with saved presets. game.mu is held.

import (
	"encoding/json"
	"fmt"
	"log"
	"os"
	"sort"
	"strings"
	"time"
)

func (g *game) hostTool(c *client, m inMsg) {
	switch m.T {
	case "host_pause":
		g.setPaused(m.On)

	// In a game: test hits and heals (the Unity version's F / S / R and Heal
	// buttons), and taking capture points by hand.
	case "host_damage":
		g.hostDamage(c, m.Tank, m.Dir)
	case "host_heal":
		g.hostHeal(c, m.Tank)
	case "host_capture":
		if g.phase != phasePlaying || g.match == nil || m.Point < 0 || m.Point > 2 {
			return
		}
		// Cycles nobody → first team → second team → nobody.
		next := map[int]int{-1: 0, 0: 1, 1: -1}[g.match.cpOwners[m.Point]]
		g.capture(m.Point, next, "")

	// Any time: one tank's settings.
	case "host_tank_flags":
		if m.Flags != nil {
			g.tanks.setFlags(m.Tank, *m.Flags)
		}
	case "host_tank_rename":
		name := cleanName(m.Name)
		if name == "" || !g.tankOnline(m.Tank) {
			return
		}
		log.Printf("[Game] host renamed %s to %s", g.tankNames[m.Tank], name)
		g.tanks.rename(m.Tank, name)
		g.tankNames[m.Tank] = name
	case "host_tank_team":
		if g.phase == phasePlaying || g.phase == phaseCountdown {
			g.sendError(c, "Change teams between games.")
			return
		}
		if g.settings.TankTeams == nil {
			g.settings.TankTeams = map[string]int{}
		}
		if m.Team == 0 || m.Team == 1 {
			g.settings.TankTeams[m.Tank] = m.Team
		} else {
			delete(g.settings.TankTeams, m.Tank)
		}
		saveSettings(g.settingsPath, g.settings)
	case "host_tank_ping":
		g.pingSent[m.Tank] = time.Now()
		delete(g.pingMs, m.Tank)
		g.tanks.sendTo(m.Tank, `{"cmd":"ping"}`)
	case "host_tank_identify":
		// A quick light show so the host can see which tank this is.
		g.tanks.sendTo(m.Tank, `{"cmd":"flash_capture"}`)
	case "host_tank_turret":
		// Hold-to-turn test, only for tanks not in a running game.
		if g.match != nil && (g.phase == phasePlaying || g.phase == phaseCountdown) && g.match.fighters[m.Tank] != nil {
			return
		}
		g.tanks.sendTo(m.Tank, fmt.Sprintf(`{"cmd":"turret","speed":%.3f}`, clampUnit(m.S)))
	case "host_tank_resync":
		g.sendTuning(m.Tank, false)

	// Advanced tuning presets.
	case "host_preset_save":
		g.savePreset(c, m.Name)
	case "host_preset_load":
		g.loadPreset(c, m.Name)
	case "host_preset_delete":
		ps := g.readPresets()
		delete(ps, m.Name)
		g.writePresets(ps)
	case "host_tuning_reset":
		s := g.settings
		s.Tuning = defaultTuning()
		g.applySettings(s)
	}
}

func (g *game) hostDamage(c *client, tankID, dir string) {
	if g.phase != phasePlaying || g.match == nil {
		g.sendError(c, "Test hits work during a game.")
		return
	}
	t := g.match.fighters[tankID]
	if t == nil {
		return
	}
	masks := map[string]byte{"N": 1 << 0, "E": 1 << 2, "S": 1 << 4, "W": 1 << 6}
	mask, ok := masks[dir]
	if !ok {
		return
	}
	dmg, killed := g.applyHit(nil, t, mask, dir)
	if dmg > 0 && !killed {
		if dir == "S" {
			g.tanks.sendTo(tankID, `{"cmd":"flash_hit","rear":true}`)
		} else {
			g.tanks.sendTo(tankID, `{"cmd":"flash_hit"}`)
		}
	}
	g.sendSetHp(t)
}

// hostHeal brings a dead tank straight back, or refills a live one.
func (g *game) hostHeal(c *client, tankID string) {
	if g.phase != phasePlaying || g.match == nil {
		g.sendError(c, "Healing works during a game.")
		return
	}
	t := g.match.fighters[tankID]
	if t == nil {
		return
	}
	if t.life != alive {
		g.respawn(t)
		return
	}
	t.hp = g.settings.MaxHp
	g.match.lowHpWarned[t.tank] = false
	g.tanks.sendTo(tankID, `{"cmd":"flash_heal"}`)
	g.sendSetHp(t)
}

// ── Tuning ───────────────────────────────────────────────────────────────────

func b2i(b bool) int {
	if b {
		return 1
	}
	return 0
}

func physicsCmd(t Tuning) string {
	return fmt.Sprintf(`{"cmd":"set_physics","drive_accel":%g,"drive_decel":%g,"turret_accel":%g,"turret_decel":%g,`+
		`"turret_min":%g,"turret_pulse_ms":%d,"turret_kick_ms":%d,"turret_brake":%d,"turret_backlash_ms":%d,`+
		`"drive_min":%g,"drive_pulse_ms":%d,"drive_kick_ms":%d,"drive_brake":%d,"drive_backlash_ms":%d}`,
		t.DriveAccel, t.DriveDecel, t.TurretAccel, t.TurretDecel,
		t.TurretMin, t.TurretPulseMs, t.TurretKickMs, b2i(t.TurretBrake), t.TurretBacklashMs,
		t.DriveMin, t.DrivePulseMs, t.DriveKickMs, b2i(t.DriveBrake), t.DriveBacklashMs)
}

// sendTuning sends one tank the host's motor, buzzer and camera settings.
// On connect the camera settings are only sent if the tank's saved ones
// differ (changing them restarts the camera's stream for a moment).
func (g *game) sendTuning(id string, onConnect bool) {
	t := g.settings.Tuning
	g.tanks.sendTo(id, physicsCmd(t))
	g.tanks.sendTo(id, fmt.Sprintf(`{"cmd":"set_buzzer","enabled":%t}`, t.Buzzer))
	want := [3]int{t.VideoFps, t.VideoFrameSize, t.VideoQuality}
	for _, ti := range g.tanks.list() {
		if ti.ID == id && (!onConnect || (ti.Video != [3]int{} && ti.Video != want)) {
			g.tanks.setVideo(id, want[0], want[1], want[2])
		}
	}
}

// pushTuning sends every tank whatever changed since old.
func (g *game) pushTuning(old Tuning) {
	t := g.settings.Tuning
	phys := physicsCmd(t) != physicsCmd(old)
	buzz := t.Buzzer != old.Buzzer
	video := t.VideoFps != old.VideoFps || t.VideoFrameSize != old.VideoFrameSize || t.VideoQuality != old.VideoQuality
	for _, ti := range g.tanks.list() {
		if phys {
			g.tanks.sendTo(ti.ID, physicsCmd(t))
		}
		if buzz {
			g.tanks.sendTo(ti.ID, fmt.Sprintf(`{"cmd":"set_buzzer","enabled":%t}`, t.Buzzer))
		}
		if video {
			g.tanks.setVideo(ti.ID, t.VideoFps, t.VideoFrameSize, t.VideoQuality)
		}
	}
}

// ── Presets: named copies of the Advanced tuning, in presets.json next to
// the settings file. ─────────────────────────────────────────────────────────

// readPresets returns a copy of the presets (read from the file once).
func (g *game) readPresets() map[string]Tuning {
	if g.presets == nil {
		g.presets = map[string]Tuning{}
		if b, err := os.ReadFile(g.presetsPath); err == nil {
			_ = json.Unmarshal(b, &g.presets)
		}
	}
	ps := make(map[string]Tuning, len(g.presets))
	for k, v := range g.presets {
		ps[k] = v
	}
	return ps
}

func (g *game) writePresets(ps map[string]Tuning) {
	g.presets = ps
	b, _ := json.MarshalIndent(ps, "", "  ")
	tmp := g.presetsPath + ".tmp"
	if err := os.WriteFile(tmp, b, 0o600); err != nil {
		log.Printf("[Presets] can't write %s: %v", tmp, err)
		return
	}
	if err := os.Rename(tmp, g.presetsPath); err != nil {
		log.Printf("[Presets] can't replace %s: %v", g.presetsPath, err)
	}
}

func (g *game) presetNames() []string {
	var names []string
	for n := range g.readPresets() {
		names = append(names, n)
	}
	sort.Slice(names, func(i, j int) bool { return strings.ToLower(names[i]) < strings.ToLower(names[j]) })
	return names
}

func (g *game) savePreset(c *client, name string) {
	name = cleanName(name)
	if name == "" {
		g.sendError(c, "Give the preset a name.")
		return
	}
	ps := g.readPresets()
	if len(ps) >= 30 {
		if _, ok := ps[name]; !ok {
			g.sendError(c, "Too many presets: delete one first.")
			return
		}
	}
	ps[name] = g.settings.Tuning
	g.writePresets(ps)
	log.Printf("[Presets] saved %q", name)
}

func (g *game) loadPreset(c *client, name string) {
	t, ok := g.readPresets()[name]
	if !ok {
		g.sendError(c, "That preset is gone.")
		return
	}
	s := g.settings
	s.Tuning = t
	g.applySettings(s)
	log.Printf("[Presets] loaded %q", name)
}
