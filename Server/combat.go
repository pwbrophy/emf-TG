package main

// Combat: hit points, damage, death and respawn, team points and the winner.
// A port of the Unity version's rules (GameService, and the combat parts of
// PlayerWebSocketServer), so games play exactly as they did:
//
//   - A hit does DamagePerHit, ×RearMultiplier from behind (S) or
//     ×SideMultiplier from the side (E/W).
//   - At 0 HP a tank explodes: motors off for ExplosionSeconds. Then it may
//     drive but not fire ("return to base") until it scans its own team's
//     base tag, which respawns it at full HP.
//   - Scanning your own base while alive refills your HP.
//   - After a respawn or refill a tank can't be hurt for InvulnerableSeconds.
//   - Each kill earns the killer's team TeamPointsPerKill; reaching
//     MaxTeamPoints wins at once. When time runs out the team with more
//     points wins; on a tie, more tanks still in the fight, then more damage.
//
// Everything here runs with game.mu held.

import (
	"fmt"
	"log"
	"math"
	"sort"
	"strings"
	"time"
)

type lifeState int

const (
	alive     lifeState = iota
	exploding           // just died: motors off
	returning           // "return to base": can drive, can't fire, can't be hurt
)

func (l lifeState) String() string {
	switch l {
	case exploding:
		return "dead"
	case returning:
		return "returning"
	}
	return "alive"
}

var teamNames = [2]string{"Desert Squad", "Jungle Squad"}

// teamIndex: 0 Desert, 1 Jungle, -1 none, from the tank's name.
func teamIndex(tankName string) int {
	switch defaultTeam(tankName) {
	case teamNames[0]:
		return 0
	case teamNames[1]:
		return 1
	}
	return -1
}

func teamName(i int) string {
	if i == 0 || i == 1 {
		return teamNames[i]
	}
	return ""
}

// combatant is one tank in the current game, with its player's numbers.
type combatant struct {
	tank, tankName string
	device, name   string // the player driving it
	team           int

	hp          int
	life        lifeState
	diedAt      time.Time
	invulnUntil time.Time // zero = not invulnerable

	kills, deaths, damage, vpFromKills int
	damageFrom                         map[string]int // shooter's name → damage they did to this player
}

func (c *combatant) invulnerable(now time.Time) bool {
	return !c.invulnUntil.IsZero() && now.Before(c.invulnUntil)
}

type match struct {
	gen        int // which game this is: shots from an earlier game are dropped
	fighters   map[string]*combatant // by tank id
	teamPoints [2]int
	teamDamage [2]int
	winner     int    // -1 = none
	endCode    string // "time", "points", "manual" (host ended or closed)

	lastCountdownTick int // last countdown number sent to the tanks
}

// rfidScan is one tag read, kept for the host's "RFID scans" list (to find a
// base tag's UID).
type rfidScan struct {
	At   string `json:"at"`
	Tank string `json:"tank"`
	UID  string `json:"uid"`
	What string `json:"what"` // "Desert Squad base", "Jungle Squad base", or ""
}

func (g *game) newMatch(players []*player) {
	g.matchGen++
	m := &match{gen: g.matchGen, fighters: map[string]*combatant{}, winner: -1}
	for _, p := range players {
		name := g.tankNames[p.tank]
		m.fighters[p.tank] = &combatant{
			tank: p.tank, tankName: name, device: p.device, name: p.name,
			team: teamIndex(name), hp: g.settings.MaxHp, damageFrom: map[string]int{},
		}
	}
	g.match = m
}

func (g *game) fighterByDevice(device string) *combatant {
	if g.match == nil {
		return nil
	}
	for _, c := range g.match.fighters {
		if c.device == device {
			return c
		}
	}
	return nil
}

// resolveCardinal turns the 8-bit sensor mask into the side a hit came from
// (N front, E right, S rear, W left) by averaging the triggered sensors'
// directions. Bit 0 = N, 1 = NE, 2 = E, ... 7 = NW.
func resolveCardinal(mask byte) string {
	if mask == 0 {
		return "N"
	}
	angles := [8]float64{90, 45, 0, -45, -90, -135, 180, 135}
	var sinSum, cosSum float64
	for i := 0; i < 8; i++ {
		if mask&(1<<i) == 0 {
			continue
		}
		rad := angles[i] * math.Pi / 180
		sinSum += math.Sin(rad)
		cosSum += math.Cos(rad)
	}
	if math.Abs(sinSum) < 1e-6 && math.Abs(cosSum) < 1e-6 {
		return "N"
	}
	deg := math.Atan2(sinSum, cosSum) * 180 / math.Pi
	if deg < 0 {
		deg += 360
	}
	switch {
	case deg >= 45 && deg <= 135:
		return "N"
	case deg > 135 && deg <= 225:
		return "W"
	case deg > 225 && deg < 315:
		return "S"
	}
	return "E"
}

// applyHit damages target and returns the damage done (0 if it couldn't be
// hurt) and whether it died.
func (g *game) applyHit(shooter, target *combatant, mask byte, dir string) (int, bool) {
	now := time.Now()
	if target.life != alive || target.invulnerable(now) {
		return 0, false
	}
	mult := 1.0
	switch dir {
	case "S":
		mult = g.settings.RearMultiplier
	case "E", "W":
		mult = g.settings.SideMultiplier
	}
	// Unity's Mathf.RoundToInt rounds halves to even; so does this.
	dmg := int(math.RoundToEven(float64(g.settings.DamagePerHit) * mult))
	target.hp = max(0, target.hp-dmg)

	if shooter.team >= 0 {
		g.match.teamDamage[shooter.team] += dmg
	}
	shooter.damage += dmg
	target.damageFrom[shooter.name] += dmg

	// The target's phone: directional hit flash and "Hit by …".
	g.sendTo(target.device, map[string]any{"t": "hit_taken", "shooter": shooter.name, "dir": dir})

	if target.hp > 0 {
		return dmg, false
	}
	g.kill(target, shooter)
	return dmg, true
}

func (g *game) kill(target, shooter *combatant) {
	m := g.match
	target.life = exploding
	target.diedAt = time.Now()
	target.deaths++
	g.tanks.sendTo(target.tank, `{"cmd":"drive","l":0,"r":0}`)
	g.tanks.sendTo(target.tank, `{"cmd":"motors_off"}`)
	g.tanks.sendTo(target.tank, `{"cmd":"flash_death"}`)
	g.sendTo(target.device, map[string]any{"t": "you_are_dead"})

	if shooter == nil || shooter.team < 0 {
		log.Printf("[Combat] %s destroyed", target.name)
		return
	}
	log.Printf("[Combat] %s destroyed %s", shooter.name, target.name)
	shooter.kills++
	shooter.vpFromKills += g.settings.TeamPointsPerKill
	m.teamPoints[shooter.team] += g.settings.TeamPointsPerKill
	if m.teamPoints[shooter.team] >= g.settings.MaxTeamPoints && g.phase == phasePlaying {
		m.winner = shooter.team
		g.endGame("points")
	}
}

// tickCombat moves exploded tanks on to "return to base" and ends
// invulnerability. Called by the game clock.
func (g *game) tickCombat(now time.Time) bool {
	if g.match == nil || g.phase != phasePlaying {
		return false
	}
	changed := false
	explosion := time.Duration(g.settings.ExplosionSeconds * float64(time.Second))
	for _, c := range g.match.fighters {
		if c.life == exploding && now.Sub(c.diedAt) >= explosion {
			c.life = returning
			g.tanks.sendTo(c.tank, `{"cmd":"motors_on"}`)
			changed = true
		}
		if !c.invulnUntil.IsZero() && !now.Before(c.invulnUntil) {
			c.invulnUntil = time.Time{}
			g.tanks.sendTo(c.tank, `{"cmd":"invuln_end"}`)
			changed = true
		}
	}
	return changed
}

func (g *game) grantInvulnerability(c *combatant) {
	now := time.Now()
	if c.invulnerable(now) {
		return
	}
	c.invulnUntil = now.Add(time.Duration(g.settings.InvulnerableSeconds * float64(time.Second)))
	g.tanks.sendTo(c.tank, `{"cmd":"invuln_start"}`)
}

func (g *game) baseTeam(uid string) int {
	for _, u := range g.settings.DesertBaseUids {
		if strings.EqualFold(u, uid) {
			return 0
		}
	}
	for _, u := range g.settings.JungleBaseUids {
		if strings.EqualFold(u, uid) {
			return 1
		}
	}
	return -1
}

// rfidScanned handles a tank reading an RFID tag: respawn or refill at its
// own base. (Capture points come later.)
func (g *game) rfidScanned(tankID, uid string) {
	uid = strings.ToUpper(strings.TrimSpace(uid))
	base := g.baseTeam(uid)
	what := ""
	if base >= 0 {
		what = teamNames[base] + " base"
	}
	name := g.tankNames[tankID]
	if name == "" {
		name = tankID
	}
	g.rfidLog = append(g.rfidLog, rfidScan{At: time.Now().Format("15:04:05"), Tank: name, UID: uid, What: what})
	if len(g.rfidLog) > 10 {
		g.rfidLog = g.rfidLog[len(g.rfidLog)-10:]
	}

	if g.match == nil || g.phase != phasePlaying {
		return
	}
	c := g.match.fighters[tankID]
	if c == nil {
		return
	}
	switch {
	case c.life == exploding:
		return // ignores everything while exploding
	case c.life == returning:
		if base >= 0 && base == c.team {
			c.life = alive
			c.hp = g.settings.MaxHp
			g.tanks.sendTo(c.tank, `{"cmd":"flash_heal"}`)
			g.sendSetHp(c)
			g.grantInvulnerability(c)
			g.sendTo(c.device, map[string]any{"t": "you_are_alive"})
			g.sendTo(c.device, map[string]any{"t": "rfid", "text": "You have respawned!"})
			log.Printf("[Combat] %s respawned", c.name)
		}
	case base >= 0 && base == c.team:
		if c.invulnerable(time.Now()) {
			return // healed moments ago: repeated scans of the same tag
		}
		c.hp = g.settings.MaxHp
		g.tanks.sendTo(c.tank, `{"cmd":"flash_heal"}`)
		g.sendSetHp(c)
		g.grantInvulnerability(c)
		g.sendTo(c.device, map[string]any{"t": "rfid", "text": "Hit points restored!"})
	case base >= 0:
		g.sendTo(c.device, map[string]any{"t": "rfid", "text": "This is not your base"})
	}
}

func (g *game) sendSetHp(c *combatant) {
	g.tanks.sendTo(c.tank, fmt.Sprintf(`{"cmd":"set_hp","hp":%d,"max":%d}`, c.hp, g.settings.MaxHp))
}

// winnerOnTime: more team points; on a tie, more tanks still in the fight
// (a tank returning to base counts, an exploding one doesn't); then more
// damage dealt. -1 if nobody.
func (g *game) winnerOnTime() int {
	m := g.match
	if m.teamPoints[0] != m.teamPoints[1] {
		if m.teamPoints[0] > m.teamPoints[1] {
			return 0
		}
		return 1
	}
	surviving := map[int]int{}
	for _, c := range m.fighters {
		if c.team < 0 || c.life == exploding {
			continue
		}
		surviving[c.team]++
	}
	best, bestN, bestDmg := -1, -1, -1
	for _, team := range []int{0, 1} {
		n, ok := surviving[team]
		if !ok {
			continue
		}
		if n > bestN || (n == bestN && m.teamDamage[team] > bestDmg) {
			best, bestN, bestDmg = team, n, m.teamDamage[team]
		}
	}
	return best
}

// combatResults lists every player's numbers for the results screens.
func (g *game) combatResults() []result {
	var out []result
	if g.match == nil {
		return out
	}
	for _, c := range g.match.fighters {
		nemesis, worst := "", 0
		for who, dmg := range c.damageFrom {
			if dmg > worst || (dmg == worst && who < nemesis) {
				nemesis, worst = who, dmg
			}
		}
		out = append(out, result{
			Name: c.name, Tank: c.tankName, Team: teamName(c.team),
			Kills: c.kills, Deaths: c.deaths, Damage: c.damage,
			VictoryPoints: c.vpFromKills, Nemesis: nemesis,
		})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].VictoryPoints != out[j].VictoryPoints {
			return out[i].VictoryPoints > out[j].VictoryPoints
		}
		if out[i].Kills != out[j].Kills {
			return out[i].Kills > out[j].Kills
		}
		return out[i].Name < out[j].Name
	})
	return out
}
