package main

// The turn-based game (Settings.GameMode = "turns"), in the style of X-COM.
//
//   - Alliance → army → player → tanks. Each player is one army in an
//     alliance and claims tanks of that alliance in the lobby; they control
//     one tank at a time (player.tank) and switch between them.
//   - Alliances take turns (the first chosen at random, then in the order of
//     the alliance list). All of an alliance's armies play their turn at the
//     same time; only the active alliance's tanks can move or fire.
//   - Each tank gets Turns.ApPerTurn action points at the start of its
//     alliance's turn. Driving spends DriveApPerSec per second (both tracks
//     at full speed; one track half that), metered here from the drive
//     commands the hub relays with the tank's 500 ms watchdog; turning the
//     turret TurretApPerSec; a shot FireAp. Unused points are lost.
//   - A turn ends when every army is done (END TURN, out of points, all on
//     overwatch, or its player gone) or its time runs out; then a short
//     handover, and the next alliance's turn.
//   - Overwatch: a tank keeps its points and, during enemy turns, sends
//     silent IR probes (ir.go) while enemies move. The first moving enemy
//     it hits takes a normal hit, and that is the tank's one overwatch shot.
//   - A destroyed tank is out for the rest of the game. An alliance wins by
//     being the last with tanks, by reaching the points to win (kills, and
//     capture points held at the start of each of its turns), or on points
//     when the round limit is reached.
//
// The rules shared with the realtime game (damage, HP...) have their own
// turn-based values (Settings.Turns, read through game.rules()).
//
// Everything here runs with game.mu held, except nextProbe, which takes it.

import (
	"fmt"
	"log"
	"math"
	"math/rand/v2"
	"slices"
	"sort"
	"time"
)

const (
	apEpsilon = 0.01 // less than this is no action points left

	// Overwatch: probes are sent while an enemy moved within probeWhileMoving,
	// and only tanks that moved within movingTargetFor count as hit (a tank
	// coasts to a stop after its last drive command).
	probeWhileMoving = 500 * time.Millisecond
	movingTargetFor  = 700 * time.Millisecond
)

// turnTank is one tank's action points and overwatch.
type turnTank struct {
	ap        float64
	overwatch bool

	driveRate  float64   // action points per second the tracks are spending
	turretRate float64   // and the turret
	driveAt    time.Time // last drive command: the tracks stop driveWatchdog after it
	meterAt    time.Time // points are spent up to here
	lastMoveAt time.Time // last drive command that moved the tracks
}

// turnState is whose turn it is.
type turnState struct {
	round         int
	order         []int // alliance indexes in turn order
	pos           int   // order[pos] is the active alliance
	active        int
	endsAt        time.Time // zero = no time limit
	handover      bool      // between turns
	handoverUntil time.Time
	probeNext     int // overwatch tanks take turns probing
}

func roundAp(v float64) float64 { return math.Round(v*10) / 10 }

// turnBased: is the game being played (or, between games, the next one)
// turn-based?
func (g *game) turnBased() bool {
	if g.match != nil && g.phase != phaseIdle && g.phase != phaseLobby {
		return g.match.turns != nil
	}
	return g.settings.GameMode == modeTurns
}

// rules are the current game's rules: the realtime or turn-based values.
func (g *game) rules() Rules {
	return g.settings.rules(g.turnBased())
}

// tanksHeld: the army's tanks, or the one tank a realtime player has.
func (p *player) tanksHeld() []string {
	if len(p.army) > 0 {
		return p.army
	}
	if p.tank != "" {
		return []string{p.tank}
	}
	return nil
}

func (p *player) clearTanks() {
	p.tank, p.role, p.allowGunner = "", "", false
	p.army, p.armyAlliance, p.turnDone = nil, "", false
}

// ── The lobby: armies and their tanks ───────────────────────────────────────

func (g *game) sortArmy(p *player) {
	sort.Slice(p.army, func(i, j int) bool { return naturalLess(g.tankNames[p.army[i]], g.tankNames[p.army[j]]) })
}

// joinArmy makes a lobby player an army in an alliance, with its first free
// tank (they claim more themselves).
func (g *game) joinArmy(c *client, p *player, allianceID string) {
	if p.armyAlliance == allianceID {
		return
	}
	a := g.allianceIndex(allianceID)
	tanks := g.tanks.list()
	sort.Slice(tanks, func(i, j int) bool { return naturalLess(tanks[i].Name, tanks[j].Name) })
	for _, t := range tanks {
		if g.allianceOf(t.ID) == a && g.tankOwner(t.ID) == nil {
			g.releaseTank(p)
			p.armyAlliance, p.army = allianceID, []string{t.ID}
			log.Printf("[Turns] %s joined %s: %s", p.name, g.allianceName(a), t.Name)
			return
		}
	}
	g.sendError(c, "No free tanks in "+g.allianceName(a)+".")
}

// claimTank adds a free tank of the player's alliance to their army.
func (g *game) claimTank(c *client, tankID string) {
	p := g.lobbyPlayer(c.device)
	if p == nil {
		if g.phase != phaseLobby {
			g.sendError(c, "Wait for the next game to choose tanks.")
		}
		return
	}
	if !g.turnBased() {
		return
	}
	if !g.tankOnline(tankID) {
		g.sendError(c, "That tank isn't connected.")
		return
	}
	a := g.allianceOf(tankID)
	if a < 0 {
		g.sendError(c, "That tank isn't in an alliance.")
		return
	}
	if other := g.tankOwner(tankID); other != nil {
		if other != p {
			g.sendError(c, other.name+" already has that tank.")
		}
		return
	}
	switch mine := g.allianceIndex(p.armyAlliance); {
	case p.armyAlliance == "":
		g.releaseTank(p)
		p.armyAlliance = g.settings.Alliances[a].ID // claiming a tank joins its alliance
	case mine != a:
		g.sendError(c, "That tank is in "+g.allianceName(a)+": you're in "+g.allianceName(mine)+".")
		return
	}
	if limit := g.settings.Turns.MaxTanksPerArmy; limit > 0 && len(p.army) >= limit {
		g.sendError(c, fmt.Sprintf("An army can have at most %d tanks.", limit))
		return
	}
	p.army = append(p.army, tankID)
	g.sortArmy(p)
}

// unclaimTank gives one of the army's tanks back. The last one stays: leave
// the alliance instead.
func (g *game) unclaimTank(c *client, tankID string) {
	p := g.lobbyPlayer(c.device)
	if p == nil || !slices.Contains(p.army, tankID) {
		return
	}
	if len(p.army) == 1 {
		g.sendError(c, "You need at least one tank: claim another first, or leave your alliance.")
		return
	}
	p.army = slices.DeleteFunc(p.army, func(id string) bool { return id == tankID })
}

// fixArmies drops tanks that are offline or now in another alliance, and
// armies whose alliance was removed. Before a game starts, and when the host
// changes alliances.
func (g *game) fixArmies() {
	online := map[string]bool{}
	for _, t := range g.tanks.list() {
		online[t.ID] = true
	}
	for _, p := range g.players {
		if p.status != stLobby || p.armyAlliance == "" {
			continue
		}
		a := g.allianceIndex(p.armyAlliance)
		if a < 0 {
			p.clearTanks()
			continue
		}
		p.army = slices.DeleteFunc(p.army, func(id string) bool { return !online[id] || g.allianceOf(id) != a })
	}
}

// armyAlliances counts the alliances these players' armies are in.
func (g *game) armyAlliances(ready []*player) int {
	seen := map[string]bool{}
	for _, p := range ready {
		if p.armyAlliance != "" && len(p.army) > 0 {
			seen[p.armyAlliance] = true
		}
	}
	return len(seen)
}

// hostTankArmy: the host gives a lobby tank to an army (target = the
// player's device) or frees it (target "").
func (g *game) hostTankArmy(c *client, tankID, target string) {
	if g.phase != phaseLobby || !g.turnBased() {
		g.sendError(c, "Move tanks between armies in the lobby.")
		return
	}
	if owner := g.tankOwner(tankID); owner != nil {
		owner.army = slices.DeleteFunc(owner.army, func(id string) bool { return id == tankID })
	}
	if target == "" {
		return
	}
	p := g.players[target]
	a := g.allianceOf(tankID)
	if p == nil || p.status != stLobby || a < 0 {
		return
	}
	if p.armyAlliance == "" || g.allianceIndex(p.armyAlliance) != a {
		p.clearTanks()
		p.armyAlliance = g.settings.Alliances[a].ID
	}
	p.army = append(p.army, tankID)
	g.sortArmy(p)
}

// ── Turns ────────────────────────────────────────────────────────────────────

// startTurns begins the first turn: every alliance with tanks takes part,
// the first one chosen at random.
func (g *game) startTurns() {
	ts := g.match.turns
	present := map[int]bool{}
	for _, c := range g.match.fighters {
		if c.alliance >= 0 {
			present[c.alliance] = true
		}
	}
	ts.order = nil
	for a := range g.settings.Alliances {
		if present[a] {
			ts.order = append(ts.order, a)
		}
	}
	if len(ts.order) == 0 {
		return
	}
	first := rand.IntN(len(ts.order))
	ts.order = append(ts.order[first:], ts.order[:first]...)
	ts.pos, ts.round = 0, 1
	g.beginTurn(time.Now())
}

// beginTurn starts the turn of order[pos]: its tanks get their action points
// and come off overwatch, and it scores the capture points it holds.
func (g *game) beginTurn(now time.Time) {
	m := g.match
	ts := m.turns
	tr := g.settings.Turns
	ts.active = ts.order[ts.pos]
	ts.handover = false
	ts.endsAt = time.Time{}
	if tr.TurnSeconds > 0 {
		ts.endsAt = now.Add(time.Duration(tr.TurnSeconds) * time.Second)
	}
	for _, p := range g.players {
		p.turnDone = false
	}
	for _, c := range m.fighters {
		if c.alliance != ts.active || c.life != alive {
			continue
		}
		c.turn = turnTank{ap: tr.ApPerTurn, meterAt: now}
	}
	g.updateProbing()
	log.Printf("[Turns] round %d: %s's turn", ts.round, g.allianceName(ts.active))
	g.displayEvent(fmt.Sprintf("Round %d: %s's turn!", ts.round, g.allianceName(ts.active)))
	g.scoreCapturePoints(ts.active) // may win the game
}

// scoreCapturePoints: at the start of its turn an alliance earns
// CapturePointsPerTurn for each point it holds.
func (g *game) scoreCapturePoints(a int) {
	m := g.match
	pts := g.settings.Turns.CapturePointsPerTurn
	if pts <= 0 {
		return
	}
	var held []int
	for i, owner := range m.cpOwners {
		if owner == a {
			held = append(held, i)
		}
	}
	for _, i := range held {
		if st := m.stats[m.cpCaptors[i]]; st != nil {
			st.vpFromCaptures += float64(pts)
		}
		g.beaconVpRipple(i)
	}
	if len(held) > 0 {
		g.addPoints(a, pts*len(held))
	}
}

// nextTurnPos is the next alliance in the order that still has tanks, and
// whether that starts a new round.
func (g *game) nextTurnPos() (pos int, newRound bool) {
	ts := g.match.turns
	n := len(ts.order)
	for i := 1; i <= n; i++ {
		p := (ts.pos + i) % n
		if g.aliveTanks(ts.order[p]) > 0 {
			return p, ts.pos+i >= n
		}
	}
	return ts.pos, true
}

func (g *game) aliveTanks(alliance int) int {
	n := 0
	for _, c := range g.match.fighters {
		if c.alliance == alliance && c.life == alive {
			n++
		}
	}
	return n
}

// endTurn stops the active alliance's tanks (their unused points are lost;
// tanks on overwatch keep theirs) and starts the handover.
func (g *game) endTurn(now time.Time) {
	m := g.match
	ts := m.turns
	for _, c := range m.fighters {
		if c.alliance != ts.active || c.life != alive {
			continue
		}
		g.haltTank(c, now)
		if !c.turn.overwatch {
			c.turn.ap = 0
		}
	}
	for _, p := range g.players {
		p.turnDone = false
	}
	log.Printf("[Turns] %s's turn is over", g.allianceName(ts.active))
	if _, newRound := g.nextTurnPos(); newRound && g.settings.Turns.MaxRounds > 0 && ts.round >= g.settings.Turns.MaxRounds {
		g.endGame("rounds")
		return
	}
	ts.handover = true
	ts.handoverUntil = now.Add(time.Duration(g.settings.Turns.HandoverSeconds) * time.Second)
	if g.settings.Turns.HandoverSeconds == 0 {
		g.advanceTurn(now)
	}
}

func (g *game) advanceTurn(now time.Time) {
	ts := g.match.turns
	pos, newRound := g.nextTurnPos()
	if newRound {
		ts.round++
	}
	ts.pos = pos
	g.beginTurn(now)
}

// tickTurns runs the turn clock (every 200 ms): action points spent by
// moving tanks, the end of a turn, the handover. True = the phones need a
// fresh snapshot (also while points are draining, so the bars move).
func (g *game) tickTurns(now time.Time) bool {
	m := g.match
	ts := m.turns
	changed := false
	for _, c := range m.fighters {
		if c.turn.driveRate == 0 && c.turn.turretRate == 0 {
			continue
		}
		g.meterAp(c, now)
		changed = true
		if c.turn.ap <= apEpsilon {
			g.haltTank(c, now) // out of action points
		}
	}
	g.updateProbing()
	if ts.handover {
		if !now.Before(ts.handoverUntil) {
			g.advanceTurn(now)
			return true
		}
		return changed
	}
	if (!ts.endsAt.IsZero() && !now.Before(ts.endsAt)) || g.allianceDone(ts.active) {
		g.endTurn(now)
		return true
	}
	return changed
}

// turnRemaining is the turn's (or the handover's) time left; 0 = no limit.
func (g *game) turnRemaining() time.Duration {
	ts := g.match.turns
	ref := time.Now()
	if g.paused {
		ref = g.pausedAt
	}
	switch {
	case ts.handover:
		return max(0, ts.handoverUntil.Sub(ref))
	case ts.endsAt.IsZero():
		return 0
	}
	return max(0, ts.endsAt.Sub(ref))
}

// resumeTurns: after a pause the turn's clock carries on where it stopped,
// and no action points are charged for the pause (the tanks were stopped).
func (g *game) resumeTurns(now time.Time, paused time.Duration) {
	if g.match == nil || g.match.turns == nil {
		return
	}
	ts := g.match.turns
	if !ts.endsAt.IsZero() {
		ts.endsAt = ts.endsAt.Add(paused)
	}
	ts.handoverUntil = ts.handoverUntil.Add(paused)
	for _, c := range g.match.fighters {
		c.turn.driveRate, c.turn.turretRate, c.turn.meterAt = 0, 0, now
	}
}

// ── Armies during a turn ─────────────────────────────────────────────────────

func (g *game) playerAlliance(p *player) int {
	if p == nil || p.armyAlliance == "" {
		return -1
	}
	return g.allianceIndex(p.armyAlliance)
}

// armyDone: the player tapped END TURN, has no tank that can still act, or
// has no page open.
func (g *game) armyDone(p *player) bool {
	if p.turnDone || p.clients == 0 {
		return true
	}
	for _, id := range p.army {
		if c := g.match.fighters[id]; c != nil && c.life == alive && !c.turn.overwatch && c.turn.ap > apEpsilon {
			return false
		}
	}
	return true
}

// allianceDone: every army of the alliance is done.
func (g *game) allianceDone(a int) bool {
	for _, p := range g.players {
		if p.status == stPlaying && g.playerAlliance(p) == a && !g.armyDone(p) {
			return false
		}
	}
	return true
}

func (g *game) setTurnDone(device string, on bool) {
	p := g.players[device]
	if p == nil || p.status != stPlaying || g.phase != phasePlaying || g.match == nil || g.match.turns == nil || g.paused {
		return
	}
	ts := g.match.turns
	if ts.handover || g.playerAlliance(p) != ts.active {
		return
	}
	p.turnDone = on
	if on {
		now := time.Now()
		for _, id := range p.army {
			if c := g.match.fighters[id]; c != nil && c.life == alive {
				g.haltTank(c, now)
			}
		}
	}
}

// selectTank: the player controls another of their tanks. The one they
// leave stops.
func (g *game) selectTank(device, tankID string) {
	p := g.players[device]
	if p == nil || p.status != stPlaying || g.match == nil || g.match.turns == nil || p.tank == tankID {
		return
	}
	c := g.match.fighters[tankID]
	if c == nil || !slices.Contains(p.army, tankID) || c.life != alive {
		return
	}
	if old := g.match.fighters[p.tank]; old != nil && old.life == alive {
		g.haltTank(old, time.Now())
	}
	p.tank = tankID
}

// turnCanAct: may this tank move or fire now?
func (g *game) turnCanAct(c *combatant) bool {
	ts := g.match.turns
	if ts == nil || ts.handover || g.paused || c.alliance != ts.active || c.life != alive || c.turn.overwatch {
		return false
	}
	p := g.players[c.driver.device]
	return p != nil && !p.turnDone
}

// ── Action points ────────────────────────────────────────────────────────────

// meterAp charges the points spent since the last charge. The tracks run
// until driveWatchdog after the last drive command, as on the tank; the
// turret keeps turning until told to stop.
func (g *game) meterAp(c *combatant, now time.Time) {
	t := &c.turn
	if !t.meterAt.IsZero() && now.After(t.meterAt) {
		trackEnd := t.driveAt.Add(driveWatchdog)
		if trackEnd.After(now) {
			trackEnd = now
		}
		cost := t.driveRate*max(0, trackEnd.Sub(t.meterAt).Seconds()) + t.turretRate*now.Sub(t.meterAt).Seconds()
		t.ap = max(0, t.ap-cost)
		if !now.Before(t.driveAt.Add(driveWatchdog)) {
			t.driveRate = 0 // the tracks have stopped by themselves
		}
	}
	t.meterAt = now
}

// stopAp: the tank stopped (or was told to): charge what it spent and stop
// charging.
func (g *game) stopAp(c *combatant, now time.Time) {
	if g.match == nil || g.match.turns == nil {
		return
	}
	g.meterAp(c, now)
	c.turn.driveRate, c.turn.turretRate = 0, 0
}

// haltTank stops a tank's tracks and turret, and its spending.
func (g *game) haltTank(c *combatant, now time.Time) {
	g.stopAp(c, now)
	g.tanks.sendTo(c.tank, `{"cmd":"drive","l":0,"r":0}`)
	g.tanks.sendTo(c.tank, `{"cmd":"turret","speed":0}`)
	c.stab.driveL, c.stab.driveR, c.stab.turretIn = 0, 0, 0
}

// chargeDrive: may the player's drive command go to the tank? It starts
// charging for it.
func (g *game) chargeDrive(c *combatant, l, r float64, now time.Time) bool {
	if !g.turnCanAct(c) {
		return false
	}
	g.meterAp(c, now)
	speed := (math.Abs(l) + math.Abs(r)) / 2
	moving := speed > 0.01
	if moving && c.turn.ap <= apEpsilon {
		g.haltTank(c, now)
		return false
	}
	c.turn.driveRate = g.settings.Turns.DriveApPerSec * speed
	c.turn.driveAt = now
	if moving {
		c.turn.lastMoveAt = now
	}
	return true
}

func (g *game) chargeTurret(c *combatant, s float64, now time.Time) bool {
	if !g.turnCanAct(c) {
		return false
	}
	g.meterAp(c, now)
	rate := g.settings.Turns.TurretApPerSec * math.Abs(s)
	if rate > 0 && c.turn.ap <= apEpsilon {
		g.haltTank(c, now)
		return false
	}
	c.turn.turretRate = rate
	return true
}

// payForShot: the tank can fire now and has the points: they're spent.
func (g *game) payForShot(c *combatant) bool {
	if !g.turnCanAct(c) {
		return false
	}
	g.meterAp(c, time.Now())
	cost := g.settings.Turns.FireAp
	if c.turn.ap+1e-9 < cost {
		g.sendTo(c.driver.device, map[string]any{"t": "fire_result", "text": "Not enough action points"})
		return false
	}
	c.turn.ap = max(0, c.turn.ap-cost)
	return true
}

// repairAtBase: a tank scanning its own base during its turn is repaired
// to full HP, which uses all its action points.
func (g *game) repairAtBase(c *combatant) {
	ts := g.match.turns
	if ts.handover || c.alliance != ts.active {
		g.sendCrew(c, map[string]any{"t": "rfid", "text": "Repair at your base during your turn"})
		return
	}
	maxHp := g.rules().MaxHp
	if c.hp >= maxHp {
		g.sendCrew(c, map[string]any{"t": "rfid", "text": "Already at full strength"})
		return
	}
	g.haltTank(c, time.Now())
	c.hp = maxHp
	c.turn.ap = 0
	g.match.lowHpWarned[c.tank] = false
	g.tanks.sendTo(c.tank, `{"cmd":"flash_heal"}`)
	g.sendSetHp(c)
	g.sendCrew(c, map[string]any{"t": "rfid", "text": "Repaired! That used this tank's action points."})
}

// ── Overwatch ────────────────────────────────────────────────────────────────

func (g *game) setOverwatch(cl *client, on bool) {
	c := g.seat(cl.device)
	if c == nil || g.match.turns == nil {
		return
	}
	if !g.settings.Turns.Overwatch {
		g.sendError(cl, "Overwatch is switched off.")
		return
	}
	ts := g.match.turns
	if ts.handover || c.alliance != ts.active || c.life != alive {
		return
	}
	now := time.Now()
	if !on {
		c.turn.overwatch = false
		g.updateProbing()
		return
	}
	if c.turn.overwatch || !g.turnCanAct(c) {
		return
	}
	g.meterAp(c, now)
	if cost := g.settings.Turns.FireAp; c.turn.ap+1e-9 < cost {
		g.sendError(cl, fmt.Sprintf("Overwatch needs %g action points.", cost))
		return
	}
	g.haltTank(c, now)
	c.turn.overwatch = true
	log.Printf("[Turns] %s is on overwatch", c.tankName)
	g.updateProbing()
}

// updateProbing tells the IR scheduler whether any tank is on overwatch.
func (g *game) updateProbing() {
	on := false
	if m := g.match; m != nil && m.turns != nil && g.phase == phasePlaying && g.settings.Turns.Overwatch {
		for _, c := range m.fighters {
			if c.turn.overwatch && c.life == alive {
				on = true
				break
			}
		}
	}
	g.ir.probing.Store(on)
}

// movingTarget: an enemy overwatch can catch (one that has just moved).
func (g *game) movingTarget(c *combatant, now time.Time) bool {
	return c.life == alive && !c.turn.lastMoveAt.IsZero() && now.Sub(c.turn.lastMoveAt) <= movingTargetFor
}

// nextProbe is the next overwatch probe to send, if any: only while a tank
// of the active alliance is moving, and the tanks on overwatch take turns.
func (g *game) nextProbe() (shotRequest, bool) {
	g.mu.Lock()
	defer g.mu.Unlock()
	m := g.match
	if m == nil || m.turns == nil || g.phase != phasePlaying {
		g.ir.probing.Store(false)
		return shotRequest{}, false
	}
	ts := m.turns
	if g.paused || ts.handover || !g.settings.Turns.Overwatch {
		return shotRequest{}, false
	}
	now := time.Now()
	moving := false
	for _, c := range m.fighters {
		if c.alliance == ts.active && c.life == alive && !c.turn.lastMoveAt.IsZero() && now.Sub(c.turn.lastMoveAt) <= probeWhileMoving {
			moving = true
			break
		}
	}
	if !moving {
		return shotRequest{}, false
	}
	var ids []string
	for id, c := range m.fighters {
		if c.turn.overwatch && c.life == alive && c.alliance != ts.active {
			ids = append(ids, id)
		}
	}
	if len(ids) == 0 {
		return shotRequest{}, false
	}
	sort.Strings(ids)
	id := ids[ts.probeNext%len(ids)]
	ts.probeNext++
	return shotRequest{shooter: id, at: now, gen: m.gen, windowMs: g.settings.Turns.OverwatchWindowMs, probe: true}, true
}

// overwatchFires: a probe hit a moving enemy. The tank fires (lights,
// sound, the player's fire effect) and that is its one overwatch shot. False
// if it is no longer on overwatch.
func (g *game) overwatchFires(c *combatant) bool {
	if !c.turn.overwatch || c.life != alive {
		return false
	}
	c.turn.overwatch = false
	c.turn.ap = 0
	if p := g.players[c.driver.device]; p != nil {
		p.shots++
	}
	g.tanks.sendTo(c.tank, `{"cmd":"flash_fire"}`)
	g.sendCrew(c, map[string]any{"t": "fired", "tank": c.tank}) // tank: the player may be watching another of theirs
	g.displaySend(map[string]any{"cmd": "fire_event", "robotId": c.tank, "callsign": c.tankName})
	g.displayEvent(c.crewNames() + "'s overwatch fires!")
	log.Printf("[Turns] %s fired on overwatch", c.tankName)
	g.updateProbing()
	return true
}

// ── Destruction and winning ──────────────────────────────────────────────────

// tankDestroyed tells the player one of their tanks is out and gives them
// the next one; you_are_dead once the whole army is gone.
func (g *game) tankDestroyed(c *combatant) {
	g.syncCamera(c.tank, false)
	p := g.players[c.driver.device]
	if p == nil {
		return
	}
	g.sendTo(p.device, map[string]any{"t": "tank_destroyed", "tank": c.tank, "name": c.tankName})
	for _, id := range p.army {
		if f := g.match.fighters[id]; f != nil && f.life == alive {
			if p.tank == c.tank {
				p.tank = id
			}
			return
		}
	}
	g.sendTo(p.device, map[string]any{"t": "you_are_dead"})
}

// checkElimination ends the game when one alliance (or none) has tanks left.
func (g *game) checkElimination() {
	m := g.match
	if m == nil || m.turns == nil || g.phase != phasePlaying {
		return
	}
	left := map[int]bool{}
	for _, c := range m.fighters {
		if c.life == alive && c.alliance >= 0 {
			left[c.alliance] = true
		}
	}
	if len(left) > 1 {
		return
	}
	m.winner = -1
	for a := range left {
		m.winner = a
	}
	g.endGame("elimination")
}

// hostEndTurn: the host ends the current turn.
func (g *game) hostEndTurn(c *client) {
	if g.phase != phasePlaying || g.match == nil || g.match.turns == nil || g.match.turns.handover {
		return
	}
	log.Printf("[Turns] the host ended the turn")
	g.endTurn(time.Now())
}

// ── What the pages see ───────────────────────────────────────────────────────

type turnView struct {
	Round         int        `json:"round"`
	MaxRounds     int        `json:"maxRounds,omitempty"`
	AllianceIndex int        `json:"allianceIndex"` // whose turn it is
	Alliance      string     `json:"alliance"`
	NextIndex     int        `json:"nextIndex"` // during the handover: whose turn is next
	Next          string     `json:"next,omitempty"`
	RemainingMs   int64      `json:"remainingMs"` // the turn's (or handover's) time left
	NoLimit       bool       `json:"noLimit,omitempty"`
	Handover      bool       `json:"handover,omitempty"`
	Armies        []armyView `json:"armies"`
}

type armyView struct {
	Player        string `json:"player"`
	AllianceIndex int    `json:"allianceIndex"`
	Done          bool   `json:"done"`
	Online        bool   `json:"online"`
	TanksLeft     int    `json:"tanksLeft"`
}

// turnRulesView: what the phones need to show costs.
type turnRulesView struct {
	ApPerTurn       float64 `json:"apPerTurn"`
	DriveApPerSec   float64 `json:"driveApPerSec"`
	TurretApPerSec  float64 `json:"turretApPerSec"`
	FireAp          float64 `json:"fireAp"`
	Overwatch       bool    `json:"overwatch"`
	TurnSeconds     int     `json:"turnSeconds"`
	MaxRounds       int     `json:"maxRounds"`
	MaxTanksPerArmy int     `json:"maxTanksPerArmy"`
}

type myTankView struct {
	ID        string   `json:"id"`
	Name      string   `json:"name"`
	Online    bool     `json:"online"`
	Color     string   `json:"color,omitempty"` // lobby colour
	ColorCSS  string   `json:"colorCss,omitempty"`
	Hp        int      `json:"hp,omitempty"`
	Life      string   `json:"life,omitempty"`
	Ap        *float64 `json:"ap,omitempty"`
	Overwatch bool     `json:"overwatch,omitempty"`
}

// turnSnapshot adds the turn-based parts of a page's snapshot.
func (g *game) turnSnapshot(s *snapshot, p *player) {
	tr := g.settings.Turns
	s.Mode = modeTurns
	s.TurnRules = &turnRulesView{ApPerTurn: tr.ApPerTurn, DriveApPerSec: tr.DriveApPerSec, TurretApPerSec: tr.TurretApPerSec,
		FireAp: tr.FireAp, Overwatch: tr.Overwatch, TurnSeconds: tr.TurnSeconds, MaxRounds: tr.MaxRounds, MaxTanksPerArmy: tr.MaxTanksPerArmy}
	m := g.match
	inGame := m != nil && m.turns != nil && len(m.turns.order) > 0 && (g.phase == phasePlaying || g.phase == phaseEnded)
	if inGame {
		ts := m.turns
		tv := &turnView{Round: ts.round, MaxRounds: tr.MaxRounds, AllianceIndex: ts.active, Alliance: g.allianceName(ts.active),
			NextIndex: -1, RemainingMs: g.turnRemaining().Milliseconds(), NoLimit: tr.TurnSeconds == 0 && !ts.handover,
			Handover: ts.handover && g.phase == phasePlaying, Armies: []armyView{}}
		if tv.Handover {
			pos, _ := g.nextTurnPos()
			tv.NextIndex = ts.order[pos]
			tv.Next = g.allianceName(tv.NextIndex)
		}
		for _, q := range g.sortedPlayers() {
			if q.status != stPlaying && q.status != stFinished || len(q.army) == 0 {
				continue
			}
			av := armyView{Player: q.name, AllianceIndex: g.playerAlliance(q), Online: q.clients > 0}
			for _, id := range q.army {
				if c := m.fighters[id]; c != nil && c.life == alive {
					av.TanksLeft++
				}
			}
			if g.phase == phasePlaying && av.AllianceIndex == ts.active && !ts.handover {
				av.Done = g.armyDone(q)
			}
			tv.Armies = append(tv.Armies, av)
		}
		s.Turn = tv
	}
	if p == nil || len(p.army) == 0 {
		return
	}
	online := map[string]bool{}
	for _, t := range g.tanks.list() {
		online[t.ID] = true
	}
	for _, id := range p.army {
		v := myTankView{ID: id, Name: g.tankNames[id], Online: online[id]}
		v.Color, v.ColorCSS = g.lobbyColor(id)
		if inGame && (p.status == stPlaying || p.status == stFinished) {
			if c := m.fighters[id]; c != nil {
				ap := roundAp(c.turn.ap)
				v.Hp, v.Life, v.Ap, v.Overwatch = c.hp, c.life.String(), &ap, c.turn.overwatch
			}
		}
		s.Me.Tanks = append(s.Me.Tanks, v)
	}
	if !inGame || p.status != stPlaying {
		return
	}
	if c := m.fighters[p.tank]; c != nil {
		ap := roundAp(c.turn.ap)
		s.Me.Ap, s.Me.ApMax, s.Me.Overwatch = &ap, tr.ApPerTurn, c.turn.overwatch
	}
	ts := m.turns
	s.Me.MyTurn = g.phase == phasePlaying && !ts.handover && g.playerAlliance(p) == ts.active
	s.Me.TurnDone = p.turnDone
}
