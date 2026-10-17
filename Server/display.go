package main

// The spectator display (display.html, for a big screen): a scoreboard,
// every tank's video, and a spoken announcer. Like the Unity version it gets
// display_update snapshots plus events: display_event (announcer lines),
// kill_event, fire_event, hit_event, countdown_tick and game_over. The
// announcer lines are the Unity version's. game.mu is held throughout.

import (
	"encoding/json"
	"math"
	"sort"
	"strconv"
	"time"
)

func (g *game) hasDisplay() bool {
	for c := range g.clients {
		if c.display {
			return true
		}
	}
	return false
}

// displaySend sends one event to every display.
func (g *game) displaySend(msg map[string]any) {
	if !g.hasDisplay() {
		return
	}
	b, _ := json.Marshal(msg)
	for c := range g.clients {
		if c.display {
			c.push(b)
		}
	}
}

// displayEvent is an announcer line: shown and spoken on the display.
func (g *game) displayEvent(text string) {
	g.displaySend(map[string]any{"cmd": "display_event", "text": text})
}

type displayPlayer struct {
	Name     string `json:"name"`
	Alliance int    `json:"alliance"`
	Role     string `json:"role"`
	Partner  string `json:"partner"`
}

type displayRobot struct {
	Callsign     string `json:"callsign"`
	Player       string `json:"player"`
	GunnerPlayer string `json:"gunnerPlayer"`
	Alliance     int    `json:"alliance"`
	Hp           int    `json:"hp"`
	MaxHp        int    `json:"maxHp"`
	Dead         bool   `json:"dead"`
	VideoURL     string `json:"videoUrl"` // the tank id: display.html asks /api/video-ws?url=<id>
}

type displayPoint struct {
	Name  string `json:"name"`
	Owner int    `json:"owner"`
}

type displayUpdate struct {
	Cmd           string          `json:"cmd"`
	Phase         string          `json:"phase"`
	Paused        bool            `json:"paused"`
	Timer         float64         `json:"timer"` // seconds left
	MatchDuration int             `json:"matchDuration"`
	PlayerCount   int             `json:"playerCount"`
	MaxPlayers    int             `json:"maxPlayers"`
	TeamPoints    [2]int          `json:"teamPoints"`
	MaxTeamPoints int             `json:"maxTeamPoints"`
	AllianceNames [2]string       `json:"allianceNames"`
	Players       []displayPlayer `json:"players"`
	Robots        []displayRobot  `json:"robots"`
	CapturePoints []displayPoint  `json:"capturePoints"`
}

func (g *game) displayState() displayUpdate {
	inMatch := g.match != nil && (g.phase == phasePlaying || g.phase == phaseEnded)
	ph := string(g.phase)
	if g.phase == phaseIdle {
		ph = "mainmenu"
	}
	d := displayUpdate{
		Cmd: "display_update", Phase: ph, Paused: g.paused,
		Timer:         math.Round(g.remaining().Seconds()*10) / 10,
		MatchDuration: g.settings.MatchSeconds,
		MaxPlayers:    g.capacity(),
		MaxTeamPoints: g.settings.MaxTeamPoints,
		AllianceNames: g.settings.TeamNames,
		Players:       []displayPlayer{},
		Robots:        []displayRobot{},
	}
	if g.phase == phaseCountdown {
		d.Timer = float64(g.settings.MatchSeconds)
	}
	for _, p := range g.sortedPlayers() {
		if p.status != stLobby && p.status != stPlaying {
			continue
		}
		d.PlayerCount++
		dp := displayPlayer{Name: p.name, Alliance: -1}
		if p.tank != "" {
			dp.Alliance = g.teamOf(p.tank)
			dp.Role, dp.Partner = g.crewOf(p)
		}
		d.Players = append(d.Players, dp)
	}
	if inMatch {
		d.TeamPoints = g.match.teamPoints
		var ids []string
		for id := range g.match.fighters {
			ids = append(ids, id)
		}
		sortByName := func(i, j int) bool {
			return naturalLess(g.match.fighters[ids[i]].tankName, g.match.fighters[ids[j]].tankName)
		}
		sort.Slice(ids, sortByName)
		for _, id := range ids {
			c := g.match.fighters[id]
			r := displayRobot{Callsign: c.tankName, Player: c.driver.name, Alliance: c.team,
				Hp: c.hp, MaxHp: g.settings.MaxHp, Dead: c.life == exploding, VideoURL: id}
			if c.gunner != nil {
				r.GunnerPlayer = c.gunner.name
			}
			d.Robots = append(d.Robots, r)
		}
	}
	for i, n := range pointNames {
		owner := -1
		if inMatch {
			owner = g.match.cpOwners[i]
		}
		d.CapturePoints = append(d.CapturePoints, displayPoint{Name: n, Owner: owner})
	}
	return d
}

// broadcastDisplay sends the displays a fresh display_update.
func (g *game) broadcastDisplay() {
	g.lastDisplay = time.Now()
	if !g.hasDisplay() {
		return
	}
	b, _ := json.Marshal(g.displayState())
	for c := range g.clients {
		if c.display {
			c.push(b)
		}
	}
}

// ── The announcer (ported from the Unity version) ───────────────────────────

func (g *game) announceHit(shooter, target *combatant, dir string) {
	g.displaySend(map[string]any{"cmd": "hit_event", "robotId": target.tank, "callsign": target.tankName, "dir": dir})
	m := g.match
	if shooter != nil && dir == "S" {
		g.displayEvent("Rear hit! " + shooter.crewNames() + " hits " + target.crewNames() + " from behind!")
	}
	// Critical: once per life, at a quarter of full HP or less.
	if target.hp > 0 && target.hp <= g.settings.MaxHp/4 && !m.lowHpWarned[target.tank] {
		m.lowHpWarned[target.tank] = true
		g.displayEvent("Warning — " + target.crewNames() + "'s tank is critical!")
	}
}

func (g *game) announceKill(shooter, target *combatant) {
	m := g.match
	victim := target.crewNames()
	m.killStreak[victim] = 0
	m.lowHpWarned[target.tank] = false
	var text string
	if shooter == nil {
		text = victim + " was destroyed!"
	} else {
		name := shooter.crewNames()
		m.killStreak[name]++
		switch streak := m.killStreak[name]; {
		case !m.firstBlood:
			m.firstBlood = true
			text = "First blood — " + name + " eliminated " + victim + "!"
		case streak == 2:
			text = "Double kill — " + name + " eliminated " + victim + "!"
		case streak == 3:
			text = "Triple kill — " + name + " is on fire!"
		case streak >= 4:
			text = name + " is unstoppable! Eliminated " + victim + "!"
		default:
			text = name + " eliminated " + victim + "!"
		}
	}
	g.displayEvent(text)
	team := -1
	if shooter != nil {
		team = shooter.team
	}
	g.displaySend(map[string]any{"cmd": "kill_event", "teamIndex": team, "targetCallsign": target.tankName,
		"points": g.settings.TeamPointsPerKill})
}

func (g *game) announceCapture(point, team int, captor string) {
	if team < 0 {
		return
	}
	who := captor
	if who == "" {
		who = g.teamName(team)
	}
	g.displayEvent(who + " captured " + pointNames[point] + " Point!")
	m := g.match
	if m.cpOwners[0] == team && m.cpOwners[1] == team && m.cpOwners[2] == team {
		g.displayEvent(g.teamName(team) + " has captured all the points!")
	}
}

// announcePoints: halfway and nearly-there milestones, and lead changes
// (a team must get 3 points clear, and both must have scored).
func (g *game) announcePoints() {
	m := g.match
	target := g.settings.MaxTeamPoints
	for t := 0; t < 2; t++ {
		pts := m.teamPoints[t]
		if !m.half[t] && pts >= target/2 {
			m.half[t] = true
			g.displayEvent(g.teamName(t) + " is halfway to a points victory!")
		}
		if !m.nearly[t] && pts >= target*9/10 {
			m.nearly[t] = true
			g.displayEvent(g.teamName(t) + " is nearly victorious!")
		}
	}
	tp0, tp1 := m.teamPoints[0], m.teamPoints[1]
	if tp0 > 0 && tp1 > 0 {
		leader := -1
		switch {
		case tp0-tp1 >= 3:
			leader = 0
		case tp1-tp0 >= 3:
			leader = 1
		}
		if leader >= 0 && leader != m.leader {
			m.leader = leader
			g.displayEvent(g.teamName(leader) + " has taken the points lead!")
		}
	}
}

type statWinner struct {
	Player string `json:"player"`
	Value  any    `json:"value,omitempty"`
}

// announceGameOver: the spoken result, then the stats screen.
func (g *game) announceGameOver(winner int) {
	if g.match == nil {
		return
	}
	best := ""
	if len(g.results) > 0 && g.results[0].VictoryPoints > 0 {
		best = g.results[0].Name
	}
	if winner >= 0 {
		text := "Game over! " + g.teamName(winner) + " are victorious!"
		if best != "" {
			text += " " + best + " was the best player!"
		}
		g.displayEvent(text)
	}

	// The stats screen: who did the most of each thing.
	stats := map[string]any{}
	if best != "" {
		stats["bestPlayer"] = statWinner{Player: best, Value: g.results[0].VictoryPoints}
	}
	most := func(key string, val func(r result) float64, show func(v float64) any) {
		bi, bv := -1, 0.0
		for i, r := range g.results {
			if v := val(r); v > bv {
				bi, bv = i, v
			}
		}
		if bi >= 0 {
			stats[key] = statWinner{Player: g.results[bi].Name, Value: show(bv)}
		}
	}
	asInt := func(v float64) any { return int(v) }
	most("mostKills", func(r result) float64 { return float64(r.Kills) }, asInt)
	most("mostDeaths", func(r result) float64 { return float64(r.Deaths) }, asInt)
	most("mostDamageGiven", func(r result) float64 { return float64(r.Damage) }, asInt)
	most("mostCaptures", func(r result) float64 { return float64(r.Captures) }, asInt)
	most("mostPoints", func(r result) float64 { return float64(r.VictoryPoints) }, asInt)
	most("mostGround", func(r result) float64 { return r.DriveSeconds }, func(v float64) any { return formatSeconds(v) })
	g.displaySend(map[string]any{"cmd": "game_over", "winnerTeam": g.teamName(winner),
		"reason": g.match.endCode, "stats": stats})
}

func formatSeconds(s float64) string {
	n := int(s)
	if n >= 60 {
		return strconv.Itoa(n/60) + "m " + strconv.Itoa(n%60) + "s driving"
	}
	return strconv.Itoa(n) + "s driving"
}
