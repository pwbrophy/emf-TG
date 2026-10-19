package main

// The game: who is hosting, who is waiting, who is in the lobby, which tank
// each player has, and the countdown → playing → game over cycle.
//
// One mutex guards all of it. Phone messages and the timer tick change the
// state; after each change every open page is sent a fresh snapshot of what
// it should show (see snapshot). Phones never decide anything themselves: the
// server checks every request (is this the host? is it your tank? is the game
// running?), so a modified page can't cheat.
//
// Only one game runs at a time. The state lives in one value (not globals) so
// that several games on one hub could be added later.
//
// Shots, hits, death, capture points and scoring are in ir.go and combat.go;
// the host's tools in hosttools.go; the spectator display in display.go.

import (
	"encoding/json"
	"fmt"
	"log"
	"math"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode"
)

type phase string

const (
	phaseIdle      phase = "idle"      // no lobby open: no host yet, or the host is still setting up
	phaseLobby     phase = "lobby"     // players choose tanks
	phaseCountdown phase = "countdown" // "3, 2, 1" before a game
	phasePlaying   phase = "playing"
	phaseEnded     phase = "ended" // game over: results showing
)

type playerStatus string

const (
	stQueued   playerStatus = "queued"   // waiting for a place in the lobby
	stLobby    playerStatus = "lobby"    // in the lobby (also: in the lobby without a tank while a game runs)
	stPlaying  playerStatus = "playing"  // in the current game
	stFinished playerStatus = "finished" // played the last game; on the results screen
)

// Roles on a 2-player tank. A player alone on a tank has no role: they
// drive and shoot.
const (
	roleDriver = "driver"
	roleGunner = "gunner"
)

// A player who closes the page (or loses Wi-Fi) keeps their place this long.
const disconnectGrace = 90 * time.Second

type tankControl interface {
	list() []tankInfo
	sendTo(id, msg string) bool
	rename(id, name string)
	setFlags(id string, f tankFlags)
	setVideo(id string, fps, frameSize, quality int)
	setStreaming(id string, on bool)
}

type player struct {
	device string // random id the phone keeps in localStorage
	name   string
	status playerStatus
	tank   string // tank id, "" = none chosen
	isHost bool   // the host playing as well

	// 2-player tanks: the player who chose the tank drives it; role is
	// roleGunner for the second player who joined it. allowGunner = the
	// driver lets someone join as gunner.
	role        string
	allowGunner bool

	stabilise bool // turret stabilisation on (stabilise.go); kept between games

	clients        int       // open pages for this device
	disconnectedAt time.Time // when the last page closed; zero while connected

	// This game's numbers.
	shots       int
	driveTime   time.Duration
	lastFire    time.Time
	lastDriveAt time.Time
}

type result struct {
	Name           string  `json:"name"`
	Tank           string  `json:"tank"`
	Alliance       string  `json:"alliance"`
	AllianceIndex  int     `json:"allianceIndex"`
	Role           string  `json:"role,omitempty"`
	Kills          int     `json:"kills"`
	Deaths         int     `json:"deaths"`
	Damage         int     `json:"damage"`
	Captures       int     `json:"captures"`
	VictoryPoints  int     `json:"victoryPoints"`
	VpFromKills    int     `json:"vpFromKills"`
	VpFromCaptures int     `json:"vpFromCaptures"`
	Nemesis        string  `json:"nemesis"`
	Shots          int     `json:"shots"`
	DriveSeconds   float64 `json:"driveSeconds"`
}

type game struct {
	mu           sync.Mutex
	tanks        tankControl
	beacons      *beaconManager // nil in tests
	settingsPath string
	settings     Settings
	presetsPath  string
	presets      map[string]Tuning // nil until first read

	phase       phase
	hostDevice  string    // "" = nobody is hosting
	phaseEndsAt time.Time // end of the countdown or of the match
	paused      bool      // the host paused the game
	pausedAt    time.Time
	endReason   string // shown on the results screens
	results     []result

	players   map[string]*player // by device
	queue     []string           // devices, first in line first
	tankNames map[string]string  // id → name, remembered for tanks that went offline

	// Combat (combat.go, ir.go). match is nil outside a game; after one it
	// keeps the final score until the next game starts.
	match    *match
	matchGen int
	ir       *irScheduler
	rfidSeen map[string]rfidScan // tag UID → when and by which tank it was last read

	// Host tools: ping round trips to each tank.
	pingSent map[string]time.Time
	pingMs   map[string]int

	// Tank colours in the lobby (colors.go): tank id → its colour, and the
	// colour each tank was last told to show (index+1, 0 = none).
	tankColor  map[string]int
	nextColor  int
	colorShown map[string]int

	clients     map[*client]struct{}
	lastDisplay time.Time
}

func newGame(tanks tankControl, settingsPath string) *game {
	g := &game{
		tanks:        tanks,
		settingsPath: settingsPath,
		settings:     loadSettings(settingsPath),
		presetsPath:  filepath.Join(filepath.Dir(settingsPath), "presets.json"),
		phase:        phaseIdle,
		players:      map[string]*player{},
		tankNames:    map[string]string{},
		pingSent:     map[string]time.Time{},
		pingMs:       map[string]int{},
		rfidSeen:     map[string]rfidScan{},
		tankColor:    map[string]int{},
		colorShown:   map[string]int{},
		clients:      map[*client]struct{}{},
	}
	g.ir = newIrScheduler(g)
	return g
}

// tankMessage receives what tanks report (from tanks.go).
func (g *game) tankMessage(id string, m tankReport) {
	switch m.Cmd {
	case "ir_emit_ack":
		g.ir.reply(irReply{tank: id, ack: true, mask: -1})
	case "ir_window_result":
		mask := 0
		if m.Mask != nil {
			mask = *m.Mask
		}
		g.ir.reply(irReply{tank: id, mask: mask})
	case "rfid":
		g.mu.Lock()
		g.rfidScanned(id, m.UID)
		g.broadcast()
		// After the new list: the host's RFID tag list flashes this tag.
		if g.hostDevice != "" {
			g.sendTo(g.hostDevice, map[string]any{"t": "rfid_scanned", "uid": strings.ToUpper(strings.TrimSpace(m.UID))})
		}
		g.mu.Unlock()
	case "pong":
		g.mu.Lock()
		if t, ok := g.pingSent[id]; ok {
			g.pingMs[id] = int(time.Since(t).Milliseconds())
			delete(g.pingSent, id)
			g.broadcast()
		}
		g.mu.Unlock()
	}
}

// ── Messages from phones ─────────────────────────────────────────────────────

type inMsg struct {
	T        string     `json:"t"`
	Device   string     `json:"device"`
	Display  bool       `json:"display"` // hello from the spectator display
	Name     string     `json:"name"`
	Password string     `json:"password"` // the game password, when joining
	Tank     string     `json:"tank"`
	Target   string     `json:"target"` // a player's device, for host actions
	L        float64    `json:"l"`
	R        float64    `json:"r"`
	S        float64    `json:"s"`
	On       bool       `json:"on"`
	Force    bool       `json:"force"`
	Dir      string     `json:"dir"`      // host test damage: N, E, S
	Point    int        `json:"point"`    // capture point 0-2
	Alliance string     `json:"alliance"` // an alliance id ("" = from the tank's name)
	ID       string     `json:"id"`       // an alliance or capture point id
	Color    string     `json:"color"`
	UID      string     `json:"uid"`    // an RFID tag
	Use      string     `json:"use"`    // what a tag is for: "base:<id>", "point:<id>", ""
	Beacon   int        `json:"beacon"` // a capture point's LED beacon, 0 = none
	Flags    *tankFlags `json:"flags"`
	Settings *Settings  `json:"settings"`
}

func (g *game) handle(c *client, m inMsg) {
	g.mu.Lock()
	defer g.mu.Unlock()

	if m.T == "hello" {
		g.hello(c, m.Device, m.Display)
		return
	}
	if c.device == "" {
		return // must say hello first
	}

	switch m.T {
	// Control input arrives 20 times a second and changes nothing on screen,
	// so it skips the snapshot broadcast below.
	case "drive":
		g.drive(c.device, m.L, m.R)
		return
	case "turret":
		g.turret(c.device, m.S)
		return
	case "stabilise":
		g.setStabilise(c.device, m.On)

	case "fire":
		g.fire(c.device)
	case "join":
		g.join(c, m.Name, m.Password)
	case "leave":
		g.removePlayer(c.device)
	case "pick_tank":
		g.pickTank(c, m.Tank)
	case "pick_alliance":
		g.pickAlliance(c, m.Alliance)
	case "allow_gunner":
		g.allowGunner(c, m.On)
	case "join_gunner":
		g.joinGunner(c, m.Tank)
	case "leave_gunner":
		if p := g.lobbyPlayer(c.device); p != nil && p.role == roleGunner {
			g.releaseTank(p)
		}
	case "swap_roles":
		g.swapRoles(c)
	case "play_again":
		g.playAgain(c.device)

	case "host_login":
		g.hostLogin(c)
	default:
		if !strings.HasPrefix(m.T, "host_") {
			return
		}
		if c.device != g.hostDevice {
			g.sendError(c, "Only the host can do that.")
			return
		}
		g.hostAction(c, m)
	}
	g.promote()
	g.broadcast()
}

func (g *game) hostAction(c *client, m inMsg) {
	switch m.T {
	case "host_settings":
		if m.Settings != nil {
			g.applySettings(*m.Settings)
		}
	case "host_open":
		if g.phase == phaseIdle {
			g.setPhase(phaseLobby)
		}
	case "host_start":
		g.start(c, m.Force)
	case "host_end":
		if g.phase == phaseCountdown || g.phase == phasePlaying {
			g.endGame("manual")
		}
	case "host_lobby":
		if g.phase == phaseEnded {
			g.setPhase(phaseLobby)
		}
	case "host_close":
		g.closeGame()
	case "host_kick":
		if p := g.players[m.Target]; p != nil {
			log.Printf("[Game] host removed %s", p.name)
			g.sendTo(p.device, map[string]any{"t": "kicked"})
			g.removePlayer(p.device)
		}
	case "host_to_queue":
		if p := g.players[m.Target]; p != nil && p.status == stLobby {
			g.releaseTank(p)
			g.enqueue(p)
		}
	case "host_release":
		if p := g.players[m.Target]; p != nil && p.status == stLobby {
			g.releaseTank(p)
		}
	case "host_player_rename":
		if p := g.players[m.Target]; p != nil {
			if name := cleanName(m.Name); name != "" {
				p.name = g.uniqueName(name, p.device)
			}
		}
	case "host_play":
		g.hostPlay(c, m.On, m.Name)
	default:
		g.hostTool(c, m) // hosttools.go
	}
}

// applySettings saves settings the host's page sent. The alliance, capture
// point and tag lists have their own host messages (and a tank can add a tag
// at any moment), so the page's copies of those are ignored.
func (g *game) applySettings(s Settings) {
	s.Alliances = g.settings.Alliances
	s.TankAlliances = g.settings.TankAlliances
	s.CapturePoints = g.settings.CapturePoints
	s.RfidTags = g.settings.RfidTags
	g.saveNewSettings(s)
}

// saveNewSettings saves new settings and sends tanks any tuning that
// changed.
func (g *game) saveNewSettings(s Settings) {
	s.clamp()
	old := g.settings
	g.settings = s
	saveSettings(g.settingsPath, s)
	log.Printf("[Game] host changed settings")
	g.pushTuning(old.Tuning)
	g.syncCameras() // "keep cameras on" may have changed
}

// ── Joining and leaving ──────────────────────────────────────────────────────

func (g *game) hello(c *client, device string, display bool) {
	device = strings.TrimSpace(device)
	if device == "" || len(device) > 64 {
		return
	}
	if c.device == "" {
		c.device = device
		c.display = display
		g.clients[c] = struct{}{}
		if p := g.players[device]; p != nil && !display {
			p.clients++
			p.disconnectedAt = time.Time{}
		}
	}
	g.broadcast()
}

func (g *game) clientGone(c *client) {
	g.mu.Lock()
	defer g.mu.Unlock()
	delete(g.clients, c)
	if c.display {
		return
	}
	p := g.players[c.device]
	if p == nil {
		return
	}
	p.clients--
	if p.clients <= 0 {
		p.clients = 0
		p.disconnectedAt = time.Now()
		// A tank whose crew vanished must not keep driving.
		if p.status == stPlaying && p.tank != "" {
			g.tanks.sendTo(p.tank, `{"cmd":"drive","l":0,"r":0}`)
			g.tanks.sendTo(p.tank, `{"cmd":"turret","speed":0}`)
			if g.match != nil {
				if c := g.match.fighters[p.tank]; c != nil {
					c.stab.driveL, c.stab.driveR, c.stab.turretIn = 0, 0, 0
				}
			}
		}
	}
	g.broadcast()
}

func (g *game) join(c *client, name, password string) {
	name = cleanName(name)
	if name == "" {
		g.sendError(c, "Please enter a name.")
		return
	}
	if g.settings.ClientPassword != "" && password != g.settings.ClientPassword {
		g.sendError(c, "Wrong game password.")
		return
	}
	p := g.players[c.device]
	if p == nil {
		p = &player{device: c.device, clients: g.openClients(c.device)}
		g.players[c.device] = p
		p.name = g.uniqueName(name, c.device)
		log.Printf("[Game] %s joined", p.name)
		g.enqueue(p)
		return
	}
	p.name = g.uniqueName(name, c.device)
}

func (g *game) hostPlay(c *client, on bool, name string) {
	p := g.players[c.device]
	if !on {
		if p != nil {
			g.removePlayer(c.device)
		}
		return
	}
	name = cleanName(name)
	if name == "" {
		name = "Host"
	}
	if p == nil {
		p = &player{device: c.device, clients: g.openClients(c.device)}
		g.players[c.device] = p
	}
	p.name = g.uniqueName(name, c.device)
	p.isHost = true
	// The host doesn't queue: they go straight into the lobby.
	if p.status == "" || p.status == stQueued || p.status == stFinished {
		g.dequeue(p.device)
		p.status = stLobby
	}
}

func (g *game) removePlayer(device string) {
	p := g.players[device]
	if p == nil {
		return
	}
	switch p.status {
	case stPlaying:
		if p.tank != "" && p.role != roleGunner {
			g.stopTank(p.tank)
		}
	case stLobby:
		g.releaseTank(p) // their gunner takes the tank over
	}
	g.dequeue(device)
	delete(g.players, device)
	log.Printf("[Game] %s left", p.name)
}

func (g *game) playAgain(device string) {
	if p := g.players[device]; p != nil && p.status == stFinished {
		p.tank, p.role, p.allowGunner = "", "", false
		g.enqueue(p) // back of the queue: people already waiting go first
	}
}

// enqueue puts a player at the back of the queue.
func (g *game) enqueue(p *player) {
	g.dequeue(p.device)
	p.status = stQueued
	g.queue = append(g.queue, p.device)
}

func (g *game) dequeue(device string) {
	for i, d := range g.queue {
		if d == device {
			g.queue = append(g.queue[:i], g.queue[i+1:]...)
			return
		}
	}
}

// capacity is how many players the lobby takes before people queue: the
// host's number, or one per connected tank (two with 2-player tanks).
func (g *game) capacity() int {
	if g.settings.MaxPlayers > 0 {
		return g.settings.MaxPlayers
	}
	n := len(g.tanks.list())
	if g.settings.TwoPlayerTanks {
		n *= 2
	}
	return max(1, n)
}

// promote moves people from the front of the queue into the lobby while it
// has room. Only while the lobby is open.
func (g *game) promote() {
	if g.phase != phaseLobby {
		return
	}
	inLobby := 0
	for _, p := range g.players {
		if p.status == stLobby && !p.isHost {
			inLobby++
		}
	}
	for inLobby < g.capacity() && len(g.queue) > 0 {
		p := g.players[g.queue[0]]
		g.queue = g.queue[1:]
		if p == nil {
			continue
		}
		p.status = stLobby
		inLobby++
	}
}

// ── Tanks ────────────────────────────────────────────────────────────────────

// lobbyPlayer is the player if they may change tanks now.
func (g *game) lobbyPlayer(device string) *player {
	p := g.players[device]
	if p == nil || p.status != stLobby || g.phase != phaseLobby {
		return nil
	}
	return p
}

func (g *game) tankOnline(id string) bool {
	for _, t := range g.tanks.list() {
		if t.ID == id {
			return true
		}
	}
	return false
}

func (g *game) pickTank(c *client, tankID string) {
	p := g.players[c.device]
	if p == nil || p.status != stLobby {
		return
	}
	if g.phase != phaseLobby {
		g.sendError(c, "Wait for the next game to choose a tank.")
		return
	}
	if tankID == "" {
		g.releaseTank(p)
		return
	}
	if p.tank == tankID && p.role != roleGunner {
		return
	}
	if !g.tankOnline(tankID) {
		g.sendError(c, "That tank isn't connected.")
		return
	}
	if other := g.tankOwner(tankID); other != nil && other != p {
		g.sendError(c, other.name+" already has that tank.")
		return
	}
	g.releaseTank(p)
	p.tank = tankID
}

// allowGunner: the driver lets a second player join their tank (or not:
// the gunner is put off the tank).
func (g *game) allowGunner(c *client, on bool) {
	p := g.lobbyPlayer(c.device)
	if p == nil || p.tank == "" || p.role == roleGunner {
		return
	}
	if on && !g.settings.TwoPlayerTanks {
		g.sendError(c, "2-player tanks are switched off.")
		return
	}
	p.allowGunner = on
	if !on {
		if gu := g.gunnerOf(p.tank); gu != nil {
			gu.tank, gu.role = "", ""
		}
	}
}

func (g *game) joinGunner(c *client, tankID string) {
	p := g.lobbyPlayer(c.device)
	if p == nil {
		return
	}
	if !g.settings.TwoPlayerTanks {
		g.sendError(c, "2-player tanks are switched off.")
		return
	}
	d := g.tankOwner(tankID)
	switch {
	case d == nil:
		g.sendError(c, "Nobody is driving that tank yet: choose it to drive.")
		return
	case d == p:
		return
	case !d.allowGunner:
		g.sendError(c, d.name+" isn't taking a gunner.")
		return
	}
	if gu := g.gunnerOf(tankID); gu != nil && gu != p {
		g.sendError(c, gu.name+" is already the gunner.")
		return
	}
	g.releaseTank(p)
	p.tank, p.role = tankID, roleGunner
}

// swapRoles: driver and gunner change places.
func (g *game) swapRoles(c *client) {
	p := g.lobbyPlayer(c.device)
	if p == nil || p.tank == "" {
		return
	}
	d, gu := g.tankOwner(p.tank), g.gunnerOf(p.tank)
	if d == nil || gu == nil {
		return
	}
	d.role, gu.role = roleGunner, ""
	gu.allowGunner, d.allowGunner = true, false
}

// releaseTank takes a player off their tank. A driver's gunner takes the
// tank over (in the lobby).
func (g *game) releaseTank(p *player) {
	if p.tank == "" {
		p.role = ""
		return
	}
	if p.role == roleGunner {
		p.tank, p.role = "", ""
		return
	}
	if p.status == stPlaying {
		g.stopTank(p.tank)
	} else if gu := g.gunnerOf(p.tank); gu != nil {
		gu.role, gu.allowGunner = "", false
	}
	p.tank, p.allowGunner = "", false
}

// tankOwner is the player driving (or who chose) a tank.
func (g *game) tankOwner(tankID string) *player {
	for _, p := range g.players {
		if p.tank == tankID && p.role != roleGunner && (p.status == stLobby || p.status == stPlaying) {
			return p
		}
	}
	return nil
}

func (g *game) gunnerOf(tankID string) *player {
	for _, p := range g.players {
		if p.tank == tankID && p.role == roleGunner && (p.status == stLobby || p.status == stPlaying) {
			return p
		}
	}
	return nil
}

func (g *game) stopTank(id string) {
	g.tanks.sendTo(id, `{"cmd":"drive","l":0,"r":0}`)
	g.tanks.sendTo(id, `{"cmd":"turret","speed":0}`)
	g.tanks.sendTo(id, `{"cmd":"motors_off"}`)
}

// tanksChanged is called when a tank connects or disconnects.
func (g *game) tanksChanged() {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.promote() // automatic lobby size follows the number of tanks
	g.broadcast()
}

// tankConnected is called when a tank (re)connects: it gets the host's
// tuning, its camera on or off and its lobby colour, and a tank in the
// running game gets its motors back, so a tank that rebooted mid-game can
// carry on.
func (g *game) tankConnected(id string) {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.sendTuning(id, true)
	g.tanks.setStreaming(id, g.cameraWanted(id))
	delete(g.colorShown, id) // a rebooted tank has forgotten its colour
	g.syncTankColors()
	if g.phase == phasePlaying && g.match != nil {
		if c := g.match.fighters[id]; c != nil {
			// Exploding (and paused) tanks stay still; everyone else can drive.
			if c.life != exploding && !g.paused {
				g.tanks.sendTo(id, `{"cmd":"motors_on"}`)
			}
			g.sendSetHp(c)
			log.Printf("[Game] %s's tank reconnected mid-game", c.crewNames())
		}
	}
}

// ── Controls ─────────────────────────────────────────────────────────────────

// seat is the player's tank in the running game, if they may control it now.
func (g *game) seat(device string) *combatant {
	if g.phase != phasePlaying || g.paused || g.match == nil {
		return nil
	}
	p := g.players[device]
	if p == nil || p.status != stPlaying || p.tank == "" {
		return nil
	}
	return g.match.fighters[p.tank]
}

func (g *game) drive(device string, l, r float64) {
	c := g.seat(device)
	if c == nil || c.driver.device != device {
		return // the gunner doesn't drive
	}
	if c.life == exploding {
		return // no driving while exploding
	}
	l, r = clampUnit(l), clampUnit(r)
	g.tanks.sendTo(c.tank, fmt.Sprintf(`{"cmd":"drive","l":%.3f,"r":%.3f}`, l, r))
	c.stab.driveL, c.stab.driveR, c.stab.driveAt = l, r, time.Now() // the stabiliser follows the hull

	p := g.players[device]
	now := time.Now()
	if l*l+r*r > 0.0025 && !p.lastDriveAt.IsZero() && now.Sub(p.lastDriveAt) < 250*time.Millisecond {
		p.driveTime += now.Sub(p.lastDriveAt)
	}
	p.lastDriveAt = now
}

func (g *game) turret(device string, s float64) {
	c := g.seat(device)
	if c == nil || c.shooter().device != device {
		return // on a 2-player tank only the gunner turns the turret
	}
	s = clampUnit(s)
	if t := g.settings.Tuning; t.SlowTurretEnabled {
		s = max(-t.SlowTurretSpeed, min(s, t.SlowTurretSpeed))
	}
	c.stab.turretIn = s
	if g.stabWanted(c) && c.life != exploding {
		g.stepStabiliser(c, time.Now(), 0) // the player's speed plus the correction, sent now
		return
	}
	g.sendTurretSpeed(c, s)
}

func (g *game) fire(device string) {
	c := g.seat(device)
	if c == nil || c.shooter().device != device || c.life != alive {
		return // dead tanks can't fire; on a 2-player tank only the gunner does
	}
	p := g.players[device]
	cooldown := time.Duration(g.settings.FireCooldownMs) * time.Millisecond
	if time.Since(p.lastFire) < cooldown {
		return
	}
	p.lastFire = time.Now()
	p.shots++
	g.tanks.sendTo(c.tank, `{"cmd":"flash_fire"}`)
	// The shooter's page plays its fire beam / muzzle flash on this.
	g.sendCrew(c, map[string]any{"t": "fired"})
	g.displaySend(map[string]any{"cmd": "fire_event", "robotId": c.tank, "callsign": c.tankName})
	if !g.ir.enqueue(shotRequest{shooter: c.tank, at: time.Now(), gen: g.match.gen}) {
		g.sendTo(device, map[string]any{"t": "fire_result", "text": "Miss (lag)"})
	}
}

// ── The game cycle ───────────────────────────────────────────────────────────

// hostLogin makes this phone the host. There is no host password for now:
// tapping Host Game is enough (and takes hosting over from another phone).
func (g *game) hostLogin(c *client) {
	if g.hostDevice != "" && g.hostDevice != c.device {
		log.Printf("[Game] hosting taken over by another phone")
	} else {
		log.Printf("[Game] host signed in")
	}
	g.hostDevice = c.device
}

func (g *game) start(c *client, force bool) {
	if g.phase != phaseLobby {
		return
	}
	var ready, without []*player
	for _, p := range g.players {
		if p.status != stLobby {
			continue
		}
		if p.tank != "" {
			ready = append(ready, p)
		} else {
			without = append(without, p)
		}
	}
	if len(ready) == 0 {
		g.sendError(c, "Nobody has chosen a tank yet.")
		return
	}
	if len(without) > 0 && !force {
		names := make([]string, len(without))
		for i, p := range without {
			names[i] = p.name
		}
		sort.Strings(names)
		g.sendTo(c.device, map[string]any{"t": "confirm_start", "names": names})
		return
	}
	// Players without a tank sit this game out at the front of the queue,
	// so they're first into the lobby for the next one. (The host playing
	// too stays on the host pages.)
	sort.Slice(without, func(i, j int) bool { return strings.ToLower(without[i].name) < strings.ToLower(without[j].name) })
	var front []string
	for _, p := range without {
		if p.isHost {
			continue
		}
		g.dequeue(p.device)
		p.status = stQueued
		p.role, p.allowGunner = "", false
		front = append(front, p.device)
	}
	g.queue = append(front, g.queue...)
	for _, p := range ready {
		p.status = stPlaying
		p.shots, p.driveTime = 0, 0
		p.lastFire, p.lastDriveAt = time.Time{}, time.Time{}
	}
	g.results = nil
	g.paused = false
	g.newMatch(ready)
	log.Printf("[Game] starting with %d players", len(ready))
	if g.settings.CountdownSeconds > 0 {
		g.setPhase(phaseCountdown)
		g.phaseEndsAt = time.Now().Add(time.Duration(g.settings.CountdownSeconds) * time.Second)
		g.countdownTick()
	} else {
		g.beginPlay()
	}
}

// countdownTick sends the tanks each number of the countdown (they beep and
// count down on their LEDs).
func (g *game) countdownTick() {
	n := int(math.Ceil(time.Until(g.phaseEndsAt).Seconds()))
	if n < 1 || g.match == nil || n == g.match.lastCountdownTick {
		return
	}
	g.match.lastCountdownTick = n
	msg := fmt.Sprintf(`{"cmd":"countdown_tick","count":%d,"total":%d}`, n, g.settings.CountdownSeconds)
	for _, t := range g.tanks.list() {
		g.tanks.sendTo(t.ID, msg)
	}
	g.displaySend(map[string]any{"cmd": "countdown_tick", "count": n, "total": g.settings.CountdownSeconds})
}

func (g *game) beginPlay() {
	g.setPhase(phasePlaying)
	g.phaseEndsAt = time.Now().Add(time.Duration(g.settings.MatchSeconds) * time.Second)
	for _, t := range g.tanks.list() {
		g.tanks.sendTo(t.ID, `{"cmd":"game_start_fanfare"}`) // also clears the lobby colours
	}
	// Colours are given out afresh in the next lobby.
	g.tankColor, g.nextColor, g.colorShown = map[string]int{}, 0, map[string]int{}
	for _, p := range g.players {
		if p.status == stPlaying && p.tank != "" && p.role != roleGunner {
			g.tanks.sendTo(p.tank, `{"cmd":"motors_on"}`)
		}
	}
	g.beaconsForPhase() // all points unlit: nobody owns them yet
}

// endGame finishes the game. code is "time" (time ran out), "points" (a
// team reached the winning score; match.winner is already set) or "manual"
// (the host ended or closed it: no winner).
func (g *game) endGame(code string) {
	m := g.match
	winner := -1
	g.paused = false
	if m != nil {
		m.endCode = code
		if code == "time" {
			m.winner = g.winnerOnTime()
		}
		winner = m.winner
		for _, c := range m.fighters {
			if !c.invulnUntil.IsZero() {
				c.invulnUntil = time.Time{}
				g.tanks.sendTo(c.tank, `{"cmd":"invuln_end"}`)
			}
		}
	}
	switch code {
	case "time":
		g.endReason = "Time's up"
	case "points":
		g.endReason = fmt.Sprintf("%s reached %d points", g.allianceName(winner), g.settings.PointsToWin)
	default:
		g.endReason = "The host ended the game"
	}
	if winner >= 0 {
		g.endReason += " · " + g.allianceName(winner) + " win!"
	}

	g.results = g.combatResults()
	for _, p := range g.players {
		if p.status != stPlaying {
			continue
		}
		if p.tank != "" && p.role != roleGunner {
			g.stopTank(p.tank)
			g.tanks.sendTo(p.tank, `{"cmd":"reset_idle"}`)
		}
		for i := range g.results {
			if g.results[i].Name == p.name {
				g.results[i].Shots = p.shots
				g.results[i].DriveSeconds = float64(int(p.driveTime.Seconds()*10)) / 10
			}
		}
		p.status = stFinished
	}
	g.setPhase(phaseEnded)
	g.announceGameOver(winner)
	log.Printf("[Game] game over: %s", g.endReason)
}

// closeGame ends hosting: lobby players go back to the front of the queue
// (in their lobby order) to wait for the next host.
func (g *game) closeGame() {
	if g.phase == phaseCountdown || g.phase == phasePlaying {
		g.endGame("manual")
	}
	var back []string
	for _, p := range g.sortedPlayers() {
		if p.status == stLobby && !p.isHost {
			p.tank, p.role, p.allowGunner = "", "", false
			p.status = stQueued
			back = append(back, p.device)
		}
	}
	g.queue = append(back, g.queue...)
	if hp := g.players[g.hostDevice]; hp != nil && hp.isHost {
		delete(g.players, hp.device)
	}
	g.hostDevice = ""
	g.setPhase(phaseIdle)
	log.Printf("[Game] host closed the game")
}

func (g *game) setPhase(ph phase) {
	if g.phase == ph {
		return
	}
	g.phase = ph
	if ph == phaseLobby {
		// Tanks from the last game are free again, and back to their idle lights.
		for _, p := range g.players {
			if p.status == stFinished {
				p.tank, p.role, p.allowGunner = "", "", false
				if p.isHost {
					p.status = stLobby // the host playing too is back in the lobby
				}
			}
		}
		for _, t := range g.tanks.list() {
			g.tanks.sendTo(t.ID, `{"cmd":"reset_idle"}`)
		}
		g.colorShown = map[string]int{} // reset_idle may have cleared them: send again
	}
	if ph != phasePlaying {
		g.beaconsForPhase()
	}
	g.syncCameras()
}

// setPaused pauses or resumes the running game. While paused the tanks'
// motors are off, the clock stops, and nothing can shoot or be hurt.
func (g *game) setPaused(on bool) {
	if g.phase != phasePlaying || g.match == nil || g.paused == on {
		return
	}
	now := time.Now()
	if on {
		g.paused, g.pausedAt = true, now
		for _, c := range g.match.fighters {
			g.stopTank(c.tank)
		}
		g.syncCameras()
		g.displayEvent("Game paused")
		log.Printf("[Game] paused")
		return
	}
	// Every timer carries on from where it stopped.
	d := now.Sub(g.pausedAt)
	g.paused = false
	g.phaseEndsAt = g.phaseEndsAt.Add(d)
	for _, c := range g.match.fighters {
		if c.life == exploding {
			c.diedAt = c.diedAt.Add(d)
		} else {
			g.tanks.sendTo(c.tank, `{"cmd":"motors_on"}`)
		}
		if !c.invulnUntil.IsZero() {
			c.invulnUntil = c.invulnUntil.Add(d)
		}
	}
	g.syncCameras()
	g.displayEvent("Game resumed")
	log.Printf("[Game] resumed after %.0fs", d.Seconds())
}

// tick runs the clock: countdown → playing → game over, and forgets
// players who left for good.
func (g *game) tick(dt time.Duration) {
	g.mu.Lock()
	defer g.mu.Unlock()
	now := time.Now()
	changed := false

	switch {
	case g.phase == phaseCountdown && !now.Before(g.phaseEndsAt):
		g.beginPlay()
		changed = true
	case g.phase == phaseCountdown:
		g.countdownTick()
	case g.phase == phasePlaying && g.paused:
	case g.phase == phasePlaying && !now.Before(g.phaseEndsAt):
		g.endGame("time")
		changed = true
	case g.phase == phasePlaying:
		changed = g.tickCombat(now, dt)
	}

	for _, p := range g.players {
		if p.clients == 0 && p.status != stPlaying && !p.disconnectedAt.IsZero() &&
			now.Sub(p.disconnectedAt) > disconnectGrace {
			g.removePlayer(p.device)
			changed = true
		}
	}

	if changed {
		g.promote()
		g.broadcast()
	} else if now.Sub(g.lastDisplay) >= time.Second {
		g.broadcastDisplay() // the display's clock
	}
}

func (g *game) runClock(stop <-chan struct{}) {
	const step = 200 * time.Millisecond
	t := time.NewTicker(step)
	defer t.Stop()
	last := time.Now()
	for {
		select {
		case <-stop:
			return
		case now := <-t.C:
			g.tick(now.Sub(last))
			last = now
		}
	}
}

// remaining is the countdown or match time left.
func (g *game) remaining() time.Duration {
	if g.phase != phaseCountdown && g.phase != phasePlaying {
		return 0
	}
	if g.paused {
		return max(0, g.phaseEndsAt.Sub(g.pausedAt))
	}
	return max(0, time.Until(g.phaseEndsAt))
}

// ── What each page sees ──────────────────────────────────────────────────────

type tankView struct {
	ID            string `json:"id"`
	Name          string `json:"name"`
	Alliance      string `json:"alliance"`
	AllianceIndex int    `json:"allianceIndex"` // -1 = none
	Player        string `json:"player"`        // who has chosen it (drives it)
	Gunner        string `json:"gunner"`        // 2-player: the gunner
	AllowGunner   bool   `json:"allowGunner"`
	Online        bool   `json:"online"`
	Color         string `json:"color,omitempty"`    // lobby colour its lights show, e.g. "RED"
	ColorCSS      string `json:"colorCss,omitempty"` // that colour for the screen

	// In a game:
	Hp   int    `json:"hp,omitempty"`
	Life string `json:"life,omitempty"`

	// Host only:
	IP               string     `json:"ip,omitempty"`
	Flags            *tankFlags `json:"flags,omitempty"`
	PingMs           int        `json:"pingMs,omitempty"`
	AllianceOverride bool       `json:"allianceOverride,omitempty"` // the host chose its alliance
	LastHit          string     `json:"lastHit,omitempty"`          // side and sensor mask of the last hit
	LastHitDir       string     `json:"lastHitDir,omitempty"`       // N, E, S, W
	LastHitMask      int        `json:"lastHitMask,omitempty"`      // the 8 sensors: bit 0 N … bit 7 NW
	Hits             int        `json:"hits,omitempty"`             // hits taken this game (the compass flashes on a new one)
	Invulnerable     bool       `json:"invulnerable,omitempty"`
}

type playerView struct {
	Device   string `json:"device,omitempty"` // host only: used for kick / move to queue
	Name     string `json:"name"`
	Status   string `json:"status"`
	Tank     string `json:"tank"` // tank name
	Alliance string `json:"alliance"`
	Role     string `json:"role,omitempty"`    // 2-player tank: driver or gunner
	Partner  string `json:"partner,omitempty"` // 2-player tank: the other crew member
	Online   bool   `json:"online"`
	Host     bool   `json:"host"`
	Hp       int    `json:"hp,omitempty"`   // in a game
	Life     string `json:"life,omitempty"` // in a game: alive, dead, returning
}

type meView struct {
	IsHost        bool   `json:"isHost"`
	Status        string `json:"status"` // "" = not joined
	Name          string `json:"name"`
	Tank          string `json:"tank"`     // tank id
	TankName      string `json:"tankName"` // tank name
	Alliance      string `json:"alliance"`
	AllianceIndex int    `json:"allianceIndex"`
	QueuePos      int    `json:"queuePos"`          // 1 = next in
	Role          string `json:"role"`              // "driver", "gunner", or "" (alone on the tank)
	Partner       string `json:"partner,omitempty"` // the other crew member
	AllowGunner   bool   `json:"allowGunner"`
	TankColor     string `json:"tankColor,omitempty"`    // lobby: "your tank is glowing RED"
	TankColorCSS  string `json:"tankColorCss,omitempty"` // that colour for the screen
	Stabilise     bool   `json:"stabilise"`              // turret stabilisation is on

	// In a game:
	Hp           int    `json:"hp"`
	MaxHp        int    `json:"maxHp"`
	Life         string `json:"life"` // "alive", "dead" (exploding), "returning" (to base)
	Invulnerable bool   `json:"invulnerable"`
}

type pointView struct {
	ID           string `json:"id"`
	Name         string `json:"name"`
	Owner        int    `json:"owner"` // -1 = nobody
	Captor       string `json:"captor,omitempty"`
	Beacon       int    `json:"beacon"`       // its LED beacon: 1-3, 0 = none
	BeaconOnline bool   `json:"beaconOnline"` // that beacon is connected
	Tags         int    `json:"tags"`         // how many RFID tags it has
}

// tagView is one RFID tag in the host's list.
type tagView struct {
	UID      string `json:"uid"`
	Use      string `json:"use"`
	LastSeen string `json:"lastSeen,omitempty"` // "15:04:05"
	Tank     string `json:"tank,omitempty"`     // the tank that read it last
}

// controlsView is the joystick and turret feel, for the in-game page.
type controlsView struct {
	Deadzone        float64 `json:"deadzone"`
	StartSpeed      float64 `json:"startSpeed"`
	FullAt          float64 `json:"fullAt"`
	Curve           float64 `json:"curve"`
	AxisSnap        float64 `json:"axisSnap"`
	TurnSpeed       float64 `json:"turnSpeed"`
	SlowTurretSpeed float64 `json:"slowTurretSpeed"`
	StabAllowed     bool    `json:"stabAllowed"` // show the STAB (turret stabilisation) button
	DebugConsole    bool    `json:"debugConsole"`
}

type snapshot struct {
	T                string       `json:"t"`
	Phase            phase        `json:"phase"`
	Paused           bool         `json:"paused"`
	Hosted           bool         `json:"hosted"`
	PasswordRequired bool         `json:"passwordRequired"` // joining needs the game password
	RemainingMs      int64        `json:"remainingMs"`      // countdown or match time left
	Capacity         int          `json:"capacity"`
	FireCooldownMs   int          `json:"fireCooldownMs"`
	TwoPlayer        bool         `json:"twoPlayer"` // 2-player tanks are allowed
	Alliances        []Alliance   `json:"alliances"`
	EndReason        string       `json:"endReason,omitempty"`
	EndCode          string       `json:"endCode,omitempty"`        // "time", "points", "manual"
	WinnerAlliance   string       `json:"winnerAlliance,omitempty"` // "" = no winner
	AlliancePoints   []int        `json:"alliancePoints"`
	PointsToWin      int          `json:"pointsToWin"`
	CapturePoints    []pointView  `json:"capturePoints"`
	Controls         controlsView `json:"controls"`
	Me               meView       `json:"me"`
	Tanks            []tankView   `json:"tanks"`
	Players          []playerView `json:"players"` // lobby and game
	Queue            []playerView `json:"queue"`
	Results          []result     `json:"results,omitempty"`
	Settings         *Settings    `json:"settings,omitempty"` // host only
	RfidTags         []tagView    `json:"rfidTags,omitempty"` // host only: every known tag
	Presets          []string     `json:"presets,omitempty"`  // host only: saved tuning presets
}

func (g *game) snapshot(device string) snapshot {
	isHost := device != "" && device == g.hostDevice
	t := g.settings.Tuning
	s := snapshot{
		T:                "state",
		Phase:            g.phase,
		Paused:           g.paused,
		Hosted:           g.hostDevice != "",
		PasswordRequired: g.settings.ClientPassword != "",
		Capacity:         g.capacity(),
		FireCooldownMs:   g.settings.FireCooldownMs,
		TwoPlayer:        g.settings.TwoPlayerTanks,
		Alliances:        g.settings.Alliances,
		AlliancePoints:   make([]int, len(g.settings.Alliances)),
		EndReason:        g.endReason,
		RemainingMs:      g.remaining().Milliseconds(),
		PointsToWin:      g.settings.PointsToWin,
		Controls: controlsView{
			Deadzone: t.JoyDeadzone, StartSpeed: t.JoyStartSpeed, FullAt: t.JoyFullAt, Curve: t.JoyCurve,
			AxisSnap: t.JoyAxisSnap, TurnSpeed: t.JoyTurnSpeed, SlowTurretSpeed: t.SlowTurretSpeed,
			StabAllowed: t.StabAllowed, DebugConsole: g.settings.DebugConsole,
		},
		Me:      meView{IsHost: isHost},
		Tanks:   []tankView{},
		Players: []playerView{},
		Queue:   []playerView{},
	}
	if isHost {
		st := g.settings
		s.Settings = &st
		s.RfidTags = g.tagViews()
		s.Presets = g.presetNames()
	}
	beacons := g.beacons.connected()
	showOwners := g.match != nil && (g.phase == phasePlaying || g.phase == phaseEnded)
	for i, cp := range g.settings.CapturePoints {
		pv := pointView{ID: cp.ID, Name: cp.Name, Owner: -1, Beacon: cp.Beacon}
		if cp.Beacon >= 1 && cp.Beacon <= 3 {
			pv.BeaconOnline = beacons[cp.Beacon-1]
		}
		for _, t := range g.settings.RfidTags {
			if t.Use == "point:"+cp.ID {
				pv.Tags++
			}
		}
		if showOwners && i < len(g.match.cpOwners) {
			pv.Owner, pv.Captor = g.match.cpOwners[i], g.match.cpCaptors[i]
		}
		s.CapturePoints = append(s.CapturePoints, pv)
	}
	if g.match != nil && len(g.match.alliancePoints) == len(s.AlliancePoints) {
		s.AlliancePoints = g.match.alliancePoints
	}
	if g.match != nil && g.phase == phaseEnded {
		s.EndCode = g.match.endCode
		s.WinnerAlliance = g.allianceName(g.match.winner)
	}

	// Tanks: every connected one, plus any chosen tank that is offline.
	viewTank := func(id, name string, online bool) tankView {
		a := g.allianceOf(id)
		tv := tankView{ID: id, Name: name, Alliance: g.allianceName(a), AllianceIndex: a, Online: online}
		tv.Color, tv.ColorCSS = g.lobbyColor(id)
		if p := g.tankOwner(id); p != nil {
			tv.Player, tv.AllowGunner = p.name, p.allowGunner
		}
		if gu := g.gunnerOf(id); gu != nil {
			tv.Gunner = gu.name
		}
		if g.match != nil && (g.phase == phasePlaying || g.phase == phaseCountdown) {
			if c := g.match.fighters[id]; c != nil {
				tv.Hp, tv.Life = c.hp, c.life.String()
				if isHost {
					tv.Invulnerable = c.invulnerable(time.Now())
					if c.lastHitDir != "" {
						tv.LastHit = fmt.Sprintf("%s (sensors %08b)", c.lastHitDir, c.lastHitMask)
						tv.LastHitDir, tv.LastHitMask, tv.Hits = c.lastHitDir, int(c.lastHitMask), c.hits
					}
				}
			}
		}
		if isHost {
			tv.PingMs = g.pingMs[id]
			_, tv.AllianceOverride = g.settings.TankAlliances[id]
		}
		return tv
	}
	online := map[string]bool{}
	for _, t := range g.tanks.list() {
		online[t.ID] = true
		g.tankNames[t.ID] = t.Name
	}
	for _, t := range g.tanks.list() {
		tv := viewTank(t.ID, t.Name, true)
		if isHost {
			f := t.Flags
			tv.Flags, tv.IP = &f, t.IP
		}
		s.Tanks = append(s.Tanks, tv)
	}
	for _, p := range g.players {
		if p.tank != "" && p.role != roleGunner && !online[p.tank] && (p.status == stLobby || p.status == stPlaying) {
			s.Tanks = append(s.Tanks, viewTank(p.tank, g.tankNames[p.tank], false))
		}
	}
	sort.Slice(s.Tanks, func(i, j int) bool { return naturalLess(s.Tanks[i].Name, s.Tanks[j].Name) })

	view := func(p *player) playerView {
		v := playerView{Name: p.name, Status: string(p.status), Tank: g.tankNames[p.tank], Online: p.clients > 0, Host: p.isHost}
		if p.tank != "" {
			v.Alliance = g.allianceName(g.allianceOf(p.tank))
			v.Role, v.Partner = g.crewOf(p)
		}
		if isHost {
			v.Device = p.device
		}
		if g.match != nil && p.status == stPlaying {
			if c := g.match.fighters[p.tank]; c != nil {
				v.Hp, v.Life = c.hp, c.life.String()
			}
		}
		return v
	}
	for _, p := range g.sortedPlayers() {
		if p.status == stLobby || p.status == stPlaying {
			s.Players = append(s.Players, view(p))
		}
	}
	for _, d := range g.queue {
		if p := g.players[d]; p != nil {
			s.Queue = append(s.Queue, view(p))
		}
	}

	if p := g.players[device]; p != nil {
		s.Me.Status = string(p.status)
		s.Me.Name = p.name
		s.Me.Tank = p.tank
		s.Me.TankName = g.tankNames[p.tank]
		s.Me.AllowGunner = p.allowGunner
		s.Me.Stabilise = p.stabilise
		if p.tank != "" {
			s.Me.TankColor, s.Me.TankColorCSS = g.lobbyColor(p.tank)
		}
		if p.tank != "" {
			s.Me.AllianceIndex = g.allianceOf(p.tank)
			s.Me.Alliance = g.allianceName(s.Me.AllianceIndex)
			s.Me.Role, s.Me.Partner = g.crewOf(p)
		}
		for i, d := range g.queue {
			if d == device {
				s.Me.QueuePos = i + 1
			}
		}
		s.Me.MaxHp = g.settings.MaxHp
		if g.match != nil && (p.status == stPlaying || p.status == stFinished) {
			if c := g.match.fighters[p.tank]; c != nil {
				s.Me.Hp = c.hp
				s.Me.Life = c.life.String()
				s.Me.Invulnerable = c.invulnerable(time.Now())
			}
		}
	}
	if g.phase == phaseEnded || s.Me.Status == string(stFinished) {
		s.Results = g.results
	}
	return s
}

// crewOf is a player's role on a 2-player tank and their partner's name
// ("", "" when alone on the tank).
func (g *game) crewOf(p *player) (role, partner string) {
	if p.tank == "" {
		return "", ""
	}
	if p.role == roleGunner {
		if d := g.tankOwner(p.tank); d != nil {
			return roleGunner, d.name
		}
		return roleGunner, ""
	}
	if gu := g.gunnerOf(p.tank); gu != nil {
		return roleDriver, gu.name
	}
	return "", ""
}

// tagViews lists every known tag, the ones read most recently first.
func (g *game) tagViews() []tagView {
	out := make([]tagView, 0, len(g.settings.RfidTags))
	for _, t := range g.settings.RfidTags {
		v := tagView{UID: t.UID, Use: t.Use}
		if seen, ok := g.rfidSeen[t.UID]; ok {
			v.LastSeen, v.Tank = seen.At.Format("15:04:05"), seen.Tank
		}
		out = append(out, v)
	}
	sort.SliceStable(out, func(i, j int) bool {
		si, sj := g.rfidSeen[out[i].UID].At, g.rfidSeen[out[j].UID].At
		return si.After(sj)
	})
	return out
}

func (g *game) sortedPlayers() []*player {
	ps := make([]*player, 0, len(g.players))
	for _, p := range g.players {
		ps = append(ps, p)
	}
	sort.Slice(ps, func(i, j int) bool { return strings.ToLower(ps[i].name) < strings.ToLower(ps[j].name) })
	return ps
}

// broadcast sends every open page its snapshot (the display its update).
// Every change ends here, so it also brings the tanks' lobby colours up to
// date. Caller holds g.mu.
func (g *game) broadcast() {
	g.syncTankColors()
	cache := map[string][]byte{}
	for c := range g.clients {
		if c.display {
			continue
		}
		b, ok := cache[c.device]
		if !ok {
			b, _ = json.Marshal(g.snapshot(c.device))
			cache[c.device] = b
		}
		c.push(b)
	}
	g.broadcastDisplay()
}

func (g *game) sendTo(device string, msg any) {
	b, _ := json.Marshal(msg)
	for c := range g.clients {
		if c.device == device && !c.display {
			c.push(b)
		}
	}
}

func (g *game) sendError(c *client, text string) {
	b, _ := json.Marshal(map[string]string{"t": "error", "msg": text})
	c.push(b)
}

func (g *game) openClients(device string) int {
	n := 0
	for c := range g.clients {
		if c.device == device && !c.display {
			n++
		}
	}
	return n
}

// ── Helpers ──────────────────────────────────────────────────────────────────

// cleanName trims a typed name to printable characters, at most 16.
func cleanName(s string) string {
	var b []rune
	for _, r := range strings.TrimSpace(s) {
		if unicode.IsPrint(r) {
			b = append(b, r)
		}
		if len(b) == 16 {
			break
		}
	}
	return strings.TrimSpace(string(b))
}

// uniqueName adds a number if another player already uses the name.
func (g *game) uniqueName(name, device string) string {
	taken := func(n string) bool {
		for _, p := range g.players {
			if p.device != device && strings.EqualFold(p.name, n) {
				return true
			}
		}
		return false
	}
	if !taken(name) {
		return name
	}
	for i := 2; ; i++ {
		if n := fmt.Sprintf("%s %d", name, i); !taken(n) {
			return n
		}
	}
}

func clampUnit(v float64) float64 { return max(-1, min(1, v)) }
