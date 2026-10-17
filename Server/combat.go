package main

// Combat: hit points, damage, death and respawn, capture points, team points
// and the winner. A port of the Unity version's rules (GameService,
// CapturePointService and the combat parts of PlayerWebSocketServer), so
// games play exactly as they did:
//
//   - A hit does DamagePerHit, ×RearMultiplier from behind (S) or
//     ×SideMultiplier from the side (E/W).
//   - At 0 HP a tank explodes: motors off for ExplosionSeconds. Then it may
//     drive but not fire ("return to base") until it scans its own team's
//     base tag, which respawns it at full HP.
//   - Scanning your own base while alive refills your HP.
//   - After a respawn or refill a tank can't be hurt for InvulnerableSeconds.
//   - Scanning a capture point's tag takes it for your team. Each team earns
//     one team point every CaptureSeconds divided by the points it holds.
//   - Each kill earns the killer's team TeamPointsPerKill; reaching
//     MaxTeamPoints wins at once. When time runs out the team with more
//     points wins; on a tie, more tanks still in the fight, then more damage.
//
// A tank's crew is its driver and, on a 2-player tank, a gunner. The gunner
// fires (and gets the kills); both share the tank's deaths.
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

var pointNames = [3]string{"North", "Centre", "South"}

// teamOf is a tank's team: the host's choice if set, otherwise its name
// (Desert-… = 0, Jungle-… = 1). -1 = no team.
func (g *game) teamOf(tankID string) int {
	if t, ok := g.settings.TankTeams[tankID]; ok {
		return t
	}
	n := strings.ToLower(g.tankNames[tankID])
	switch {
	case strings.HasPrefix(n, "desert"):
		return 0
	case strings.HasPrefix(n, "jungle"):
		return 1
	}
	return -1
}

func (g *game) teamName(i int) string {
	if i == 0 || i == 1 {
		return g.settings.TeamNames[i]
	}
	return ""
}

type crewMember struct {
	device, name string
}

// combatant is one tank in the current game.
type combatant struct {
	tank, tankName string
	team           int
	driver         crewMember
	gunner         *crewMember // nil = one-player tank

	hp          int
	life        lifeState
	diedAt      time.Time
	invulnUntil time.Time // zero = not invulnerable

	lastHitDir  string // for the host's "IR sensor hit display"
	lastHitMask byte
}

func (c *combatant) invulnerable(now time.Time) bool {
	return !c.invulnUntil.IsZero() && now.Before(c.invulnUntil)
}

// shooter is the crew member who fires: the gunner on a 2-player tank.
func (c *combatant) shooter() crewMember {
	if c.gunner != nil {
		return *c.gunner
	}
	return c.driver
}

func (c *combatant) crew() []crewMember {
	if c.gunner != nil {
		return []crewMember{c.driver, *c.gunner}
	}
	return []crewMember{c.driver}
}

// crewNames: "Alice", or "Alice and Bob" for a 2-player tank.
func (c *combatant) crewNames() string {
	if c.gunner != nil {
		return c.driver.name + " and " + c.gunner.name
	}
	return c.driver.name
}

// playerStats is one player's numbers for the game.
type playerStats struct {
	name, tankName string
	team           int
	role           string // "driver", "gunner" or "" (one-player tank)
	kills, deaths  int
	damage         int
	vpFromKills    int
	vpFromCaptures float64
	captures       int
	damageFrom     map[string]int // shooter's name → damage they did to this player
}

type match struct {
	gen        int // which game this is: shots from an earlier game are dropped
	fighters   map[string]*combatant // by tank id
	stats      map[string]*playerStats
	teamPoints [2]int
	teamDamage [2]int
	winner     int    // -1 = none
	endCode    string // "time", "points", "manual" (host ended or closed)

	cpOwners    [3]int    // -1 = nobody
	cpCaptors   [3]string // player who took each point
	cpAccum     [2]float64
	cpFlashNext [2]int // round-robin: which owned point the beacon flashes next

	lastCountdownTick int // last countdown number sent to the tanks

	// Announcer bookkeeping (display.go).
	firstBlood   bool
	killStreak   map[string]int
	lowHpWarned  map[string]bool
	half, nearly [2]bool
	leader       int
}

// rfidScan is one tag read, kept for the host's "RFID scans" list (to find a
// tag's UID).
type rfidScan struct {
	At   string `json:"at"`
	Tank string `json:"tank"`
	UID  string `json:"uid"`
	What string `json:"what"` // "Desert Squad base", "North point", or ""
}

func (g *game) newMatch(players []*player) {
	g.matchGen++
	m := &match{
		gen: g.matchGen, fighters: map[string]*combatant{}, stats: map[string]*playerStats{},
		winner: -1, cpOwners: [3]int{-1, -1, -1}, killStreak: map[string]int{}, lowHpWarned: map[string]bool{}, leader: -1,
	}
	for _, p := range players {
		if p.role == roleGunner {
			continue
		}
		c := &combatant{
			tank: p.tank, tankName: g.tankNames[p.tank], team: g.teamOf(p.tank),
			driver: crewMember{p.device, p.name}, hp: g.settings.MaxHp,
		}
		m.fighters[p.tank] = c
	}
	for _, p := range players {
		if p.role == roleGunner {
			if c := m.fighters[p.tank]; c != nil {
				c.gunner = &crewMember{p.device, p.name}
			}
		}
	}
	for _, c := range m.fighters {
		for _, cm := range c.crew() {
			role := ""
			if c.gunner != nil {
				role = roleDriver
				if cm.device == c.gunner.device {
					role = roleGunner
				}
			}
			m.stats[cm.name] = &playerStats{name: cm.name, tankName: c.tankName, team: c.team, role: role, damageFrom: map[string]int{}}
		}
	}
	g.match = m
}

// fighterOf is the tank a device crews in the current game, and its role.
func (g *game) fighterOf(device string) (*combatant, string) {
	if g.match == nil {
		return nil, ""
	}
	for _, c := range g.match.fighters {
		if c.driver.device == device {
			if c.gunner != nil {
				return c, roleDriver
			}
			return c, ""
		}
		if c.gunner != nil && c.gunner.device == device {
			return c, roleGunner
		}
	}
	return nil, ""
}

func (g *game) sendCrew(c *combatant, msg map[string]any) {
	for _, cm := range c.crew() {
		g.sendTo(cm.device, msg)
	}
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

func (g *game) damageFor(dir string) int {
	mult := 1.0
	switch dir {
	case "S":
		mult = g.settings.RearMultiplier
	case "E", "W":
		mult = g.settings.SideMultiplier
	}
	// Unity's Mathf.RoundToInt rounds halves to even; so does this.
	return int(math.RoundToEven(float64(g.settings.DamagePerHit) * mult))
}

// applyHit damages target and returns the damage done (0 if it couldn't be
// hurt) and whether it died. shooter is nil for the host's test hits.
func (g *game) applyHit(shooter, target *combatant, mask byte, dir string) (int, bool) {
	now := time.Now()
	if g.paused || target.life != alive || target.invulnerable(now) {
		return 0, false
	}
	m := g.match
	dmg := g.damageFor(dir)
	target.hp = max(0, target.hp-dmg)
	target.lastHitDir, target.lastHitMask = dir, mask

	shooterName := ""
	if shooter != nil {
		shooterName = shooter.shooter().name
		if shooter.team >= 0 {
			m.teamDamage[shooter.team] += dmg
		}
		if st := m.stats[shooterName]; st != nil {
			st.damage += dmg
		}
		if st := m.stats[target.driver.name]; st != nil {
			st.damageFrom[shooterName] += dmg
		}
	}

	// The target crew's phones: directional hit flash and "Hit by …".
	g.sendCrew(target, map[string]any{"t": "hit_taken", "shooter": shooterName, "dir": dir})
	g.announceHit(shooter, target, dir)

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
	for _, cm := range target.crew() {
		if st := m.stats[cm.name]; st != nil {
			st.deaths++
		}
	}
	g.tanks.sendTo(target.tank, `{"cmd":"drive","l":0,"r":0}`)
	g.tanks.sendTo(target.tank, `{"cmd":"motors_off"}`)
	g.tanks.sendTo(target.tank, `{"cmd":"flash_death"}`)
	g.sendCrew(target, map[string]any{"t": "you_are_dead"})
	g.announceKill(shooter, target)

	if shooter == nil || shooter.team < 0 {
		log.Printf("[Combat] %s destroyed", target.crewNames())
		return
	}
	log.Printf("[Combat] %s destroyed %s", shooter.crewNames(), target.crewNames())
	if st := m.stats[shooter.shooter().name]; st != nil {
		st.kills++
		st.vpFromKills += g.settings.TeamPointsPerKill
	}
	g.addTeamPoints(shooter.team, g.settings.TeamPointsPerKill)
}

// addTeamPoints scores for a team and ends the game if it reached the target.
func (g *game) addTeamPoints(team, pts int) {
	m := g.match
	if team < 0 || pts <= 0 {
		return
	}
	m.teamPoints[team] += pts
	g.announcePoints()
	if m.teamPoints[team] >= g.settings.MaxTeamPoints && g.phase == phasePlaying {
		m.winner = team
		g.endGame("points")
	}
}

// tickCombat moves exploded tanks on to "return to base", ends
// invulnerability and scores held capture points. Called by the game clock.
func (g *game) tickCombat(now time.Time, dt time.Duration) bool {
	if g.match == nil || g.phase != phasePlaying || g.paused {
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
	if g.tickCapturePoints(dt) {
		changed = true
	}
	return changed
}

// tickCapturePoints: 1 point held → 1 team point every CaptureSeconds,
// 2 → twice as often, 3 → three times.
func (g *game) tickCapturePoints(dt time.Duration) bool {
	m := g.match
	changed := false
	for team := 0; team < 2; team++ {
		var held []int
		for i, owner := range m.cpOwners {
			if owner == team {
				held = append(held, i)
			}
		}
		if len(held) == 0 {
			m.cpAccum[team] = 0
			continue
		}
		interval := g.settings.CaptureSeconds / float64(len(held))
		m.cpAccum[team] += dt.Seconds()
		for m.cpAccum[team] >= interval && g.phase == phasePlaying {
			m.cpAccum[team] -= interval
			share := 1 / float64(len(held))
			for _, i := range held {
				if st := m.stats[m.cpCaptors[i]]; st != nil {
					st.vpFromCaptures += share
				}
			}
			// The beacons take turns flashing for each point scored.
			g.beaconVpRipple(held[m.cpFlashNext[team]%len(held)])
			m.cpFlashNext[team]++
			g.addTeamPoints(team, 1)
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

func uidIn(uid string, list []string) bool {
	for _, u := range list {
		if strings.EqualFold(u, uid) {
			return true
		}
	}
	return false
}

func (g *game) baseTeam(uid string) int {
	switch {
	case uidIn(uid, g.settings.DesertBaseUids):
		return 0
	case uidIn(uid, g.settings.JungleBaseUids):
		return 1
	}
	return -1
}

func (g *game) capturePoint(uid string) int {
	switch {
	case uidIn(uid, g.settings.NorthPointUids):
		return 0
	case uidIn(uid, g.settings.CentrePointUids):
		return 1
	case uidIn(uid, g.settings.SouthPointUids):
		return 2
	}
	return -1
}

// rfidScanned handles a tank reading an RFID tag: capturing a point,
// respawning or refilling at its own base.
func (g *game) rfidScanned(tankID, uid string) {
	uid = strings.ToUpper(strings.TrimSpace(uid))
	base, point := g.baseTeam(uid), g.capturePoint(uid)
	what := ""
	switch {
	case base >= 0:
		what = g.teamName(base) + " base"
	case point >= 0:
		what = pointNames[point] + " point"
	}
	name := g.tankNames[tankID]
	if name == "" {
		name = tankID
	}
	g.rfidLog = append(g.rfidLog, rfidScan{At: time.Now().Format("15:04:05"), Tank: name, UID: uid, What: what})
	if len(g.rfidLog) > 12 {
		g.rfidLog = g.rfidLog[len(g.rfidLog)-12:]
	}

	if g.match == nil || g.phase != phasePlaying || g.paused {
		return
	}
	c := g.match.fighters[tankID]
	if c == nil {
		return
	}
	switch c.life {
	case exploding:
		return // ignores everything while exploding
	case returning:
		if base >= 0 && base == c.team {
			g.respawn(c)
			g.sendCrew(c, map[string]any{"t": "rfid", "text": "You have respawned!"})
		}
		return // returning tanks can't capture
	}

	if point >= 0 && c.team >= 0 {
		if g.match.cpOwners[point] == c.team {
			g.sendCrew(c, map[string]any{"t": "rfid", "text": "You already captured this point!"})
		} else {
			g.capture(point, c.team, c.driver.name)
			g.tanks.sendTo(c.tank, `{"cmd":"flash_capture"}`)
			g.sendCrew(c, map[string]any{"t": "rfid", "text": "You have captured " + pointNames[point] + "!"})
		}
	}
	switch {
	case base >= 0 && base == c.team:
		if c.invulnerable(time.Now()) {
			return // healed moments ago: repeated scans of the same tag
		}
		c.hp = g.settings.MaxHp
		g.tanks.sendTo(c.tank, `{"cmd":"flash_heal"}`)
		g.sendSetHp(c)
		g.grantInvulnerability(c)
		g.sendCrew(c, map[string]any{"t": "rfid", "text": "Hit points restored!"})
	case base >= 0:
		g.sendCrew(c, map[string]any{"t": "rfid", "text": "This is not your base"})
	}
}

// capture gives a point to a team (-1 = nobody). captor "" = the host did it.
func (g *game) capture(point, team int, captor string) {
	m := g.match
	m.cpOwners[point] = team
	m.cpCaptors[point] = captor
	if team >= 0 && captor != "" {
		if st := m.stats[captor]; st != nil {
			st.captures++
		}
	}
	g.beaconShowOwner(point, team, true)
	g.announceCapture(point, team, captor)
}

func (g *game) respawn(c *combatant) {
	c.life = alive
	c.hp = g.settings.MaxHp
	g.tanks.sendTo(c.tank, `{"cmd":"flash_heal"}`)
	g.tanks.sendTo(c.tank, `{"cmd":"motors_on"}`)
	g.sendSetHp(c)
	g.grantInvulnerability(c)
	g.sendCrew(c, map[string]any{"t": "you_are_alive"})
	g.match.lowHpWarned[c.tank] = false
	g.displayEvent(c.crewNames() + " have returned to battle!")
	log.Printf("[Combat] %s respawned", c.crewNames())
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
	for _, st := range g.match.stats {
		nemesis, worst := "", 0
		for who, dmg := range st.damageFrom {
			if dmg > worst || (dmg == worst && who < nemesis) {
				nemesis, worst = who, dmg
			}
		}
		capVp := int(math.RoundToEven(st.vpFromCaptures))
		out = append(out, result{
			Name: st.name, Tank: st.tankName, Team: g.teamName(st.team), Role: st.role,
			Kills: st.kills, Deaths: st.deaths, Damage: st.damage, Captures: st.captures,
			VictoryPoints: st.vpFromKills + capVp, VpFromKills: st.vpFromKills, VpFromCaptures: capVp,
			Nemesis: nemesis,
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
