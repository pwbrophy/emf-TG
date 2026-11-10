package main

// What the game writes to the event log (sessionlog.go) at the start and end
// of each game, so a game can be read back afterwards: who played which tank,
// under which rules, how its shots went and how it ended. game.mu is held.

import (
	"sort"
	"time"
)

// shotTally adds up how one game's shots went.
type shotTally struct {
	Shots     int `json:"shots"`
	Hits      int `json:"hits"`      // at least one tank hurt
	Misses    int `json:"misses"`    // no enemy saw the left barrel
	OneBarrel int `json:"oneBarrel"` // an enemy saw the left barrel but none saw both
	NoEffect  int `json:"noEffect"`  // both barrels seen, but the tank was dead or couldn't be hurt
	NoAck     int `json:"noAck"`     // the shooter never said its barrel was lit
	Stale     int `json:"stale"`     // dropped: waited too long behind other shots
	Silent    int `json:"silent"`    // times a listening tank didn't answer in time
	SlowestMs int `json:"slowestMs"` // longest from trigger to result
	totalMs   int
}

func (t *shotTally) add(result string, silent, totalMs int) {
	t.Shots++
	switch result {
	case "hit":
		t.Hits++
	case "miss":
		t.Misses++
	case "one_barrel":
		t.OneBarrel++
	case "no_effect":
		t.NoEffect++
	case "no_ack", "shooter_offline":
		t.NoAck++
	case "stale":
		t.Stale++
	}
	t.Silent += silent
	t.SlowestMs = max(t.SlowestMs, totalMs)
	t.totalMs += totalMs
}

func (g *game) logGameStart(forced bool) {
	m := g.match
	if m == nil {
		return
	}
	m.startedAt = time.Now()
	if g.events == nil {
		return
	}
	type crew struct {
		Tank     string `json:"tank"`
		ID       string `json:"id"`
		Alliance string `json:"alliance"`
		Driver   string `json:"driver"`
		Gunner   string `json:"gunner,omitempty"`
		Online   bool   `json:"online"`
	}
	online := map[string]bool{}
	var spare []string
	for _, t := range g.tanks.list() {
		online[t.ID] = true
		if m.fighters[t.ID] == nil {
			spare = append(spare, t.Name)
		}
	}
	sort.Strings(spare)
	var tanks []crew
	for id, c := range m.fighters {
		t := crew{Tank: c.tankName, ID: id, Alliance: g.allianceName(c.alliance), Driver: c.driver.name, Online: online[id]}
		if c.gunner != nil {
			t.Gunner = c.gunner.name
		}
		tanks = append(tanks, t)
	}
	sort.Slice(tanks, func(i, j int) bool { return naturalLess(tanks[i].Tank, tanks[j].Tank) })
	s, r, t := g.settings, g.rules(), g.settings.Tuning
	g.events.add("game_start", map[string]any{
		"game": m.gen, "mode": s.GameMode, "tanks": tanks, "tanks_not_playing": spare,
		"started_despite_warnings": forced,
		"rules": map[string]any{
			"matchSeconds": s.MatchSeconds, "countdownSeconds": r.CountdownSeconds, "maxHp": r.MaxHp,
			"damagePerHit": r.DamagePerHit, "rearMultiplier": r.RearMultiplier, "sideMultiplier": r.SideMultiplier,
			"fireCooldownMs": r.FireCooldownMs, "pointsPerKill": r.PointsPerKill, "pointsToWin": r.PointsToWin,
			"captureSeconds": s.CaptureSeconds, "explosionSeconds": s.ExplosionSeconds,
			"invulnerableSeconds": s.InvulnerableSeconds, "twoPlayerTanks": s.TwoPlayerTanks,
			"irWindowMs": s.IrWindowMs, "irAckTimeoutMs": s.IrAckTimeoutMs, "irWindowTimeoutMs": s.IrWindowTimeoutMs,
			"airtimeFairness": s.AirtimeFairness, "camerasAlwaysOn": s.CamerasAlwaysOn,
			"videoFps": t.VideoFps, "videoFrameSize": t.VideoFrameSize, "videoQuality": t.VideoQuality,
		},
	})
}

func (g *game) logGameEnd(code string) {
	m := g.match
	if m == nil || g.events == nil {
		return
	}
	type final struct {
		Tank string `json:"tank"`
		Hp   int    `json:"hp"`
		Life string `json:"life"`
		Hits int    `json:"hitsTaken"`
	}
	var tanks []final
	for _, c := range m.fighters {
		tanks = append(tanks, final{c.tankName, c.hp, c.life.String(), c.hits})
	}
	sort.Slice(tanks, func(i, j int) bool { return naturalLess(tanks[i].Tank, tanks[j].Tank) })
	points := map[string]int{}
	for i, p := range m.alliancePoints {
		points[g.allianceName(i)] = p
	}
	var offline []string // players whose page was closed when the game ended
	for _, p := range g.players {
		if p.status == stFinished && p.clients == 0 {
			offline = append(offline, p.name)
		}
	}
	sort.Strings(offline)
	f := map[string]any{
		"game": m.gen, "code": code, "reason": g.endReason, "winner": g.allianceName(m.winner),
		"points": points, "results": g.results, "tanks": tanks, "shots": m.shots,
		"players_offline_at_end": offline,
	}
	if !m.playAt.IsZero() {
		f["played_s"] = round1(time.Since(m.playAt).Seconds())
	}
	if m.shots.Shots > 0 {
		f["shot_avg_ms"] = m.shots.totalMs / m.shots.Shots
	}
	g.events.add("game_end", f)
}

// healthCounts is the game's part of the hub's 30 s health line.
func (g *game) healthCounts() map[string]any {
	g.mu.Lock()
	defer g.mu.Unlock()
	online := 0
	for _, p := range g.players {
		if p.clients > 0 {
			online++
		}
	}
	return map[string]any{
		"phase": string(g.phase), "pages": len(g.clients), "players": len(g.players),
		"players_online": online, "tanks": len(g.tanks.list()),
	}
}

// settingsForLog is the host's settings for the log bundle, without the game
// password.
func (g *game) settingsForLog() Settings {
	g.mu.Lock()
	defer g.mu.Unlock()
	s := g.settings
	if s.ClientPassword != "" {
		s.ClientPassword = "(set)"
	}
	return s
}
