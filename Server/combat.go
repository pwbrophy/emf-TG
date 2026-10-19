package main

// Combat: hit points, damage, death and respawn, capture points, alliance
// points and the winner. A port of the Unity version's rules (GameService,
// CapturePointService and the combat parts of PlayerWebSocketServer), so
// games play exactly as they did:
//
//   - A hit does DamagePerHit, ×RearMultiplier from behind (S) or
//     ×SideMultiplier from the side (E/W).
//   - At 0 HP a tank explodes: motors off for ExplosionSeconds. Then it may
//     drive but not fire ("return to base") until it scans its own
//     alliance's base tag, which respawns it at full HP.
//   - Scanning your own base while alive refills your HP.
//   - After a respawn or refill a tank can't be hurt for InvulnerableSeconds.
//   - Scanning a capture point's tag takes it for your alliance. Each
//     alliance earns one point every CaptureSeconds divided by the points it
//     holds.
//   - Each kill earns the killer's alliance PointsPerKill; reaching
//     PointsToWin wins at once. When time runs out the alliance with more
//     points wins; on a tie, more tanks still in the fight, then more damage.
//
// "Alliance" is the realtime game's side (the planned turn-based mode can
// have several teams in an alliance). A tank's crew is its driver and, on a
// 2-player tank, a gunner. The gunner fires (and gets the kills); both share
// the tank's deaths.
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

// allianceOf is a tank's alliance (an index into settings.Alliances): the
// host's choice if set, otherwise the alliance its name starts with
// ("Desert-01" → id "desert", or the first word of "Desert Squad"). -1 = none.
func (g *game) allianceOf(tankID string) int {
	if id, ok := g.settings.TankAlliances[tankID]; ok {
		if i := g.allianceIndex(id); i >= 0 {
			return i
		}
	}
	return g.defaultAlliance(g.tankNames[tankID])
}

func (g *game) defaultAlliance(tankName string) int {
	n := strings.ToLower(tankName)
	if n == "" {
		return -1
	}
	for i, a := range g.settings.Alliances {
		first, _, _ := strings.Cut(strings.ToLower(a.Name), " ")
		if strings.HasPrefix(n, a.ID) || (first != "" && strings.HasPrefix(n, first)) {
			return i
		}
	}
	return -1
}

func (g *game) allianceIndex(id string) int {
	for i, a := range g.settings.Alliances {
		if a.ID == id {
			return i
		}
	}
	return -1
}

func (g *game) allianceName(i int) string {
	if i >= 0 && i < len(g.settings.Alliances) {
		return g.settings.Alliances[i].Name
	}
	return ""
}

func (g *game) pointIndex(id string) int {
	for i, p := range g.settings.CapturePoints {
		if p.ID == id {
			return i
		}
	}
	return -1
}

func (g *game) pointName(i int) string {
	if i >= 0 && i < len(g.settings.CapturePoints) {
		return g.settings.CapturePoints[i].Name
	}
	return ""
}

// tagUse is what an RFID tag is for: an alliance's base, a capture point
// (indexes), or neither (-1, -1). known says whether the hub knows the tag.
func (g *game) tagUse(uid string) (base, point int, known bool) {
	for _, t := range g.settings.RfidTags {
		if !strings.EqualFold(t.UID, uid) {
			continue
		}
		kind, id, _ := strings.Cut(t.Use, ":")
		switch kind {
		case "base":
			return g.allianceIndex(id), -1, true
		case "point":
			return -1, g.pointIndex(id), true
		}
		return -1, -1, true
	}
	return -1, -1, false
}

type crewMember struct {
	device, name string
}

// combatant is one tank in the current game.
type combatant struct {
	tank, tankName string
	alliance       int
	driver         crewMember
	gunner         *crewMember // nil = one-player tank

	hp          int
	life        lifeState
	diedAt      time.Time
	invulnUntil time.Time // zero = not invulnerable

	lastHitDir  string // for the host's "IR sensor hit display"
	hits        int    // hits taken (the host's compass flashes on each new one)
	lastHitMask byte

	stab stabState // turret stabilisation (stabilise.go)
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
	alliance       int
	role           string // "driver", "gunner" or "" (one-player tank)
	kills, deaths  int
	damage         int
	vpFromKills    int
	vpFromCaptures float64
	captures       int
	damageFrom     map[string]int // shooter's name → damage they did to this player
}

// match is one game. The alliance and capture point lists can't change
// while it runs (the host's tools refuse), so their indexes stay valid.
type match struct {
	gen            int                   // which game this is: shots from an earlier game are dropped
	fighters       map[string]*combatant // by tank id
	stats          map[string]*playerStats
	alliancePoints []int
	allianceDamage []int
	winner         int    // -1 = none
	endCode        string // "time", "points", "manual" (host ended or closed)

	cpOwners    []int     // per capture point: owning alliance, -1 = nobody
	cpCaptors   []string  // player who took each point
	cpAccum     []float64 // per alliance: seconds towards the next capture point
	cpFlashNext []int     // per alliance: which owned point the beacon flashes next

	lastCountdownTick int // last countdown number sent to the tanks

	// Announcer bookkeeping (display.go).
	firstBlood   bool
	killStreak   map[string]int
	lowHpWarned  map[string]bool
	half, nearly []bool
	leader       int
}

// rfidScan is one tag read, for the host's RFID tag list.
type rfidScan struct {
	At   time.Time
	Tank string
}

func (g *game) newMatch(players []*player) {
	g.matchGen++
	na, np := len(g.settings.Alliances), len(g.settings.CapturePoints)
	m := &match{
		gen: g.matchGen, fighters: map[string]*combatant{}, stats: map[string]*playerStats{},
		alliancePoints: make([]int, na), allianceDamage: make([]int, na), winner: -1,
		cpOwners: make([]int, np), cpCaptors: make([]string, np), cpAccum: make([]float64, na), cpFlashNext: make([]int, na),
		killStreak: map[string]int{}, lowHpWarned: map[string]bool{}, half: make([]bool, na), nearly: make([]bool, na), leader: -1,
	}
	for i := range m.cpOwners {
		m.cpOwners[i] = -1
	}
	for _, p := range players {
		if p.role == roleGunner {
			continue
		}
		m.fighters[p.tank] = &combatant{
			tank: p.tank, tankName: g.tankNames[p.tank], alliance: g.allianceOf(p.tank),
			driver: crewMember{p.device, p.name}, hp: g.settings.MaxHp,
		}
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
			m.stats[cm.name] = &playerStats{name: cm.name, tankName: c.tankName, alliance: c.alliance, role: role, damageFrom: map[string]int{}}
		}
	}
	g.match = m
}

// inMatch is true while a game is counting down or running.
func (g *game) inMatch() bool {
	return g.match != nil && (g.phase == phaseCountdown || g.phase == phasePlaying)
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
	target.hits++

	shooterName := ""
	if shooter != nil {
		shooterName = shooter.shooter().name
		if shooter.alliance >= 0 {
			m.allianceDamage[shooter.alliance] += dmg
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

	if shooter == nil || shooter.alliance < 0 {
		log.Printf("[Combat] %s destroyed", target.crewNames())
		return
	}
	log.Printf("[Combat] %s destroyed %s", shooter.crewNames(), target.crewNames())
	if st := m.stats[shooter.shooter().name]; st != nil {
		st.kills++
		st.vpFromKills += g.settings.PointsPerKill
	}
	g.addPoints(shooter.alliance, g.settings.PointsPerKill)
}

// addPoints scores for an alliance and ends the game if it reached the
// target.
func (g *game) addPoints(alliance, pts int) {
	m := g.match
	if alliance < 0 || alliance >= len(m.alliancePoints) || pts <= 0 {
		return
	}
	m.alliancePoints[alliance] += pts
	g.announcePoints()
	if m.alliancePoints[alliance] >= g.settings.PointsToWin && g.phase == phasePlaying {
		m.winner = alliance
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

// tickCapturePoints: 1 point held → 1 alliance point every CaptureSeconds,
// 2 → twice as often, and so on.
func (g *game) tickCapturePoints(dt time.Duration) bool {
	m := g.match
	changed := false
	for a := range m.alliancePoints {
		var held []int
		for i, owner := range m.cpOwners {
			if owner == a {
				held = append(held, i)
			}
		}
		if len(held) == 0 {
			m.cpAccum[a] = 0
			continue
		}
		interval := g.settings.CaptureSeconds / float64(len(held))
		m.cpAccum[a] += dt.Seconds()
		for m.cpAccum[a] >= interval && g.phase == phasePlaying {
			m.cpAccum[a] -= interval
			share := 1 / float64(len(held))
			for _, i := range held {
				if st := m.stats[m.cpCaptors[i]]; st != nil {
					st.vpFromCaptures += share
				}
			}
			// The beacons take turns flashing for each point scored.
			g.beaconVpRipple(held[m.cpFlashNext[a]%len(held)])
			m.cpFlashNext[a]++
			g.addPoints(a, 1)
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

// rfidScanned handles a tank reading an RFID tag: capturing a point,
// respawning or refilling at its own base. A tag the hub hasn't seen before
// is added to the host's RFID tag list, ready to be assigned.
func (g *game) rfidScanned(tankID, uid string) {
	uid = strings.ToUpper(strings.TrimSpace(uid))
	if uid == "" || len(uid) > 32 {
		return
	}
	base, point, known := g.tagUse(uid)
	if !known && len(g.settings.RfidTags) < maxRfidTags {
		g.settings.RfidTags = append(g.settings.RfidTags, RfidTag{UID: uid})
		saveSettings(g.settingsPath, g.settings)
		log.Printf("[RFID] new tag %s", uid)
	}
	name := g.tankNames[tankID]
	if name == "" {
		name = tankID
	}
	g.rfidSeen[uid] = rfidScan{At: time.Now(), Tank: name}

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
		if base >= 0 && base == c.alliance {
			g.respawn(c)
			g.sendCrew(c, map[string]any{"t": "rfid", "text": "You have respawned!"})
		}
		return // returning tanks can't capture
	}

	if point >= 0 && point < len(g.match.cpOwners) && c.alliance >= 0 {
		if g.match.cpOwners[point] == c.alliance {
			g.sendCrew(c, map[string]any{"t": "rfid", "text": "You already captured this point!"})
		} else {
			g.capture(point, c.alliance, c.driver.name)
			g.tanks.sendTo(c.tank, `{"cmd":"flash_capture"}`)
			g.sendCrew(c, map[string]any{"t": "rfid", "text": "You have captured " + g.pointName(point) + "!"})
		}
	}
	switch {
	case base >= 0 && base == c.alliance:
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

// capture gives a point to an alliance (-1 = nobody). captor "" = the host
// did it.
func (g *game) capture(point, alliance int, captor string) {
	m := g.match
	m.cpOwners[point] = alliance
	m.cpCaptors[point] = captor
	if alliance >= 0 && captor != "" {
		if st := m.stats[captor]; st != nil {
			st.captures++
		}
	}
	g.beaconShowOwner(point, alliance, true)
	g.announceCapture(point, alliance, captor)
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

// winnerOnTime: most points; on a tie, more tanks still in the fight (a
// tank returning to base counts, an exploding one doesn't); then more
// damage dealt. -1 if nobody.
func (g *game) winnerOnTime() int {
	m := g.match
	surviving := make([]int, len(m.alliancePoints))
	present := make([]bool, len(m.alliancePoints))
	for _, c := range m.fighters {
		if c.alliance < 0 {
			continue
		}
		present[c.alliance] = true
		if c.life != exploding {
			surviving[c.alliance]++
		}
	}
	best := -1
	better := func(a, b int) bool { // is alliance a ahead of b?
		if m.alliancePoints[a] != m.alliancePoints[b] {
			return m.alliancePoints[a] > m.alliancePoints[b]
		}
		if surviving[a] != surviving[b] {
			return surviving[a] > surviving[b]
		}
		return m.allianceDamage[a] > m.allianceDamage[b]
	}
	for a := range m.alliancePoints {
		if !present[a] && m.alliancePoints[a] == 0 {
			continue // not in this game
		}
		if best < 0 || better(a, best) {
			best = a
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
			Name: st.name, Tank: st.tankName, Alliance: g.allianceName(st.alliance), AllianceIndex: st.alliance, Role: st.role,
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
