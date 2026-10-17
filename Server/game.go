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
// Real-time rules only for now: drive, turret, fire with a cooldown (the tank
// flashes; hit detection comes later), and a match timer.

import (
	"encoding/json"
	"fmt"
	"log"
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

// A player who closes the page (or loses Wi-Fi) keeps their place this long.
const disconnectGrace = 90 * time.Second

type tankControl interface {
	list() []tankInfo
	sendTo(id, msg string) bool
}

type player struct {
	device string // random id the phone keeps in localStorage
	name   string
	status playerStatus
	tank   string // tank id, "" = none chosen
	isHost bool   // the host playing as well

	clients        int       // open pages for this device
	disconnectedAt time.Time // when the last page closed; zero while connected

	// This game's numbers.
	shots       int
	driveTime   time.Duration
	lastFire    time.Time
	lastDriveAt time.Time
}

type result struct {
	Name         string  `json:"name"`
	Tank         string  `json:"tank"`
	Team         string  `json:"team"`
	Shots        int     `json:"shots"`
	DriveSeconds float64 `json:"driveSeconds"`
}

type game struct {
	mu           sync.Mutex
	tanks        tankControl
	settingsPath string
	settings     Settings

	phase       phase
	hostDevice  string    // "" = nobody is hosting
	phaseEndsAt time.Time // end of the countdown or of the match
	endReason   string
	results     []result

	players map[string]*player // by device
	queue   []string           // devices, first in line first
	tankNames map[string]string // id → name, remembered for tanks that went offline

	clients map[*client]struct{}
}

func newGame(tanks tankControl, settingsPath string) *game {
	return &game{
		tanks:        tanks,
		settingsPath: settingsPath,
		settings:     loadSettings(settingsPath),
		phase:        phaseIdle,
		players:      map[string]*player{},
		tankNames:    map[string]string{},
		clients:      map[*client]struct{}{},
	}
}

// ── Messages from phones ─────────────────────────────────────────────────────

type inMsg struct {
	T        string    `json:"t"`
	Device   string    `json:"device"`
	Name     string    `json:"name"`
	Password string    `json:"password"`
	Tank     string    `json:"tank"`
	Target   string    `json:"target"` // a player's device, for host actions
	L        float64   `json:"l"`
	R        float64   `json:"r"`
	S        float64   `json:"s"`
	On       bool      `json:"on"`
	Force    bool      `json:"force"`
	Settings *Settings `json:"settings"`
}

func (g *game) handle(c *client, m inMsg) {
	g.mu.Lock()
	defer g.mu.Unlock()

	if m.T == "hello" {
		g.hello(c, m.Device)
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

	case "fire":
		g.fire(c.device)
	case "join":
		g.join(c, m.Name, m.Password)
	case "leave":
		g.removePlayer(c.device)
	case "pick_tank":
		g.pickTank(c, m.Tank)
	case "play_again":
		g.playAgain(c.device)

	case "host_login":
		g.hostLogin(c, m.Password)
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
			s := *m.Settings
			s.clamp()
			g.settings = s
			saveSettings(g.settingsPath, s)
			log.Printf("[Game] host changed settings")
		}
	case "host_open":
		if g.phase == phaseIdle {
			g.setPhase(phaseLobby)
		}
	case "host_start":
		g.start(c, m.Force)
	case "host_end":
		if g.phase == phaseCountdown || g.phase == phasePlaying {
			g.endGame("The host ended the game")
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
	case "host_play":
		g.hostPlay(c, m.On, m.Name)
	}
}

// ── Joining and leaving ──────────────────────────────────────────────────────

func (g *game) hello(c *client, device string) {
	device = strings.TrimSpace(device)
	if device == "" || len(device) > 64 {
		return
	}
	if c.device == "" {
		c.device = device
		g.clients[c] = struct{}{}
		if p := g.players[device]; p != nil {
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
	p := g.players[c.device]
	if p == nil {
		return
	}
	p.clients--
	if p.clients <= 0 {
		p.clients = 0
		p.disconnectedAt = time.Now()
		// A tank whose driver vanished must not keep driving.
		if p.status == stPlaying && p.tank != "" {
			g.tanks.sendTo(p.tank, `{"cmd":"drive","l":0,"r":0}`)
			g.tanks.sendTo(p.tank, `{"cmd":"turret","speed":0}`)
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
	if p.status == stPlaying && p.tank != "" {
		g.stopTank(p.tank)
	}
	g.dequeue(device)
	delete(g.players, device)
	log.Printf("[Game] %s left", p.name)
}

func (g *game) playAgain(device string) {
	if p := g.players[device]; p != nil && p.status == stFinished {
		p.tank = ""
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

// capacity is how many players the lobby takes before people queue.
func (g *game) capacity() int {
	if g.settings.MaxPlayers > 0 {
		return g.settings.MaxPlayers
	}
	return max(1, len(g.tanks.list()))
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
	online := false
	for _, t := range g.tanks.list() {
		if t.ID == tankID {
			online = true
		}
	}
	if !online {
		g.sendError(c, "That tank isn't connected.")
		return
	}
	if other := g.tankOwner(tankID); other != nil && other != p {
		g.sendError(c, other.name+" already has that tank.")
		return
	}
	p.tank = tankID
}

func (g *game) releaseTank(p *player) {
	if p.status == stPlaying && p.tank != "" {
		g.stopTank(p.tank)
	}
	p.tank = ""
}

func (g *game) tankOwner(tankID string) *player {
	for _, p := range g.players {
		if p.tank == tankID && (p.status == stLobby || p.status == stPlaying) {
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

// tankConnected is called when a tank (re)connects: a tank in the running
// game gets its motors back, so a tank that rebooted mid-game can carry on.
func (g *game) tankConnected(id string) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.phase == phasePlaying {
		if p := g.tankOwner(id); p != nil && p.status == stPlaying {
			g.tanks.sendTo(id, `{"cmd":"motors_on"}`)
			log.Printf("[Game] %s's tank reconnected mid-game", p.name)
		}
	}
}

// ── Controls ─────────────────────────────────────────────────────────────────

func (g *game) controllable(device string) *player {
	if g.phase != phasePlaying {
		return nil
	}
	p := g.players[device]
	if p == nil || p.status != stPlaying || p.tank == "" {
		return nil
	}
	return p
}

func (g *game) drive(device string, l, r float64) {
	p := g.controllable(device)
	if p == nil {
		return
	}
	l, r = clampUnit(l), clampUnit(r)
	g.tanks.sendTo(p.tank, fmt.Sprintf(`{"cmd":"drive","l":%.3f,"r":%.3f}`, l, r))

	now := time.Now()
	if l*l+r*r > 0.0025 && !p.lastDriveAt.IsZero() && now.Sub(p.lastDriveAt) < 250*time.Millisecond {
		p.driveTime += now.Sub(p.lastDriveAt)
	}
	p.lastDriveAt = now
}

func (g *game) turret(device string, s float64) {
	if p := g.controllable(device); p != nil {
		g.tanks.sendTo(p.tank, fmt.Sprintf(`{"cmd":"turret","speed":%.3f}`, clampUnit(s)))
	}
}

func (g *game) fire(device string) {
	p := g.controllable(device)
	if p == nil {
		return
	}
	cooldown := time.Duration(g.settings.FireCooldownMs) * time.Millisecond
	if time.Since(p.lastFire) < cooldown {
		return
	}
	p.lastFire = time.Now()
	p.shots++
	g.tanks.sendTo(p.tank, `{"cmd":"flash_fire"}`)
	// The shooter's page plays its fire beam / muzzle flash on this.
	g.sendTo(device, map[string]any{"t": "fired"})
}

// ── The game cycle ───────────────────────────────────────────────────────────

func (g *game) hostLogin(c *client, password string) {
	if password != g.settings.HostPassword {
		g.sendError(c, "Wrong host password.")
		return
	}
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
	for _, p := range ready {
		p.status = stPlaying
		p.shots, p.driveTime = 0, 0
		p.lastFire, p.lastDriveAt = time.Time{}, time.Time{}
	}
	g.results = nil
	log.Printf("[Game] starting with %d players", len(ready))
	if g.settings.CountdownSeconds > 0 {
		g.setPhase(phaseCountdown)
		g.phaseEndsAt = time.Now().Add(time.Duration(g.settings.CountdownSeconds) * time.Second)
	} else {
		g.beginPlay()
	}
}

func (g *game) beginPlay() {
	g.setPhase(phasePlaying)
	g.phaseEndsAt = time.Now().Add(time.Duration(g.settings.MatchSeconds) * time.Second)
	for _, p := range g.players {
		if p.status == stPlaying && p.tank != "" {
			g.tanks.sendTo(p.tank, `{"cmd":"motors_on"}`)
		}
	}
}

func (g *game) endGame(reason string) {
	g.endReason = reason
	g.results = nil
	names := map[string]string{}
	for _, t := range g.tanks.list() {
		names[t.ID] = t.Name
	}
	for _, p := range g.players {
		if p.status != stPlaying {
			continue
		}
		if p.tank != "" {
			g.stopTank(p.tank)
		}
		tankName := names[p.tank]
		if tankName == "" {
			tankName = g.tankNames[p.tank]
		}
		g.results = append(g.results, result{
			Name: p.name, Tank: tankName, Team: defaultTeam(tankName),
			Shots: p.shots, DriveSeconds: float64(int(p.driveTime.Seconds()*10)) / 10,
		})
		p.status = stFinished
	}
	sort.Slice(g.results, func(i, j int) bool { return g.results[i].Name < g.results[j].Name })
	g.setPhase(phaseEnded)
	log.Printf("[Game] game over: %s", reason)
}

// closeGame ends hosting: lobby players go back to the front of the queue
// (in their lobby order) to wait for the next host.
func (g *game) closeGame() {
	if g.phase == phaseCountdown || g.phase == phasePlaying {
		g.endGame("The host closed the game")
	}
	var back []string
	for _, p := range g.sortedPlayers() {
		if p.status == stLobby && !p.isHost {
			p.tank = ""
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
		// Tanks from the last game are free again.
		for _, p := range g.players {
			if p.status == stFinished {
				p.tank = ""
			}
		}
	}
}

// tick runs the clock: countdown → playing → game over, and forgets
// players who left for good.
func (g *game) tick() {
	g.mu.Lock()
	defer g.mu.Unlock()
	now := time.Now()
	changed := false

	switch {
	case g.phase == phaseCountdown && !now.Before(g.phaseEndsAt):
		g.beginPlay()
		changed = true
	case g.phase == phasePlaying && !now.Before(g.phaseEndsAt):
		g.endGame("Time's up")
		changed = true
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
	}
}

func (g *game) runClock(stop <-chan struct{}) {
	t := time.NewTicker(200 * time.Millisecond)
	defer t.Stop()
	for {
		select {
		case <-stop:
			return
		case <-t.C:
			g.tick()
		}
	}
}

// ── What each page sees ──────────────────────────────────────────────────────

type tankView struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	Team   string `json:"team"`
	Player string `json:"player"` // who has chosen it
	Online bool   `json:"online"`
}

type playerView struct {
	Device string `json:"device,omitempty"` // host only: used for kick / move to queue
	Name   string `json:"name"`
	Status string `json:"status"`
	Tank   string `json:"tank"` // tank name
	Online bool   `json:"online"`
	Host   bool   `json:"host"`
}

type meView struct {
	IsHost   bool   `json:"isHost"`
	Status   string `json:"status"` // "" = not joined
	Name     string `json:"name"`
	Tank     string `json:"tank"`     // tank id
	TankName string `json:"tankName"` // tank name
	Team     string `json:"team"`
	QueuePos int    `json:"queuePos"` // 1 = next in
}

type snapshot struct {
	T                string       `json:"t"`
	Phase            phase        `json:"phase"`
	Hosted           bool         `json:"hosted"`
	PasswordRequired bool         `json:"passwordRequired"`
	RemainingMs      int64        `json:"remainingMs"` // countdown or match time left
	Capacity         int          `json:"capacity"`
	FireCooldownMs   int          `json:"fireCooldownMs"`
	EndReason        string       `json:"endReason,omitempty"`
	Me               meView       `json:"me"`
	Tanks            []tankView   `json:"tanks"`
	Players          []playerView `json:"players"` // lobby and game
	Queue            []playerView `json:"queue"`
	Results          []result     `json:"results,omitempty"`
	Settings         *Settings    `json:"settings,omitempty"` // host only
}

func (g *game) snapshot(device string) snapshot {
	isHost := device != "" && device == g.hostDevice
	s := snapshot{
		T:                "state",
		Phase:            g.phase,
		Hosted:           g.hostDevice != "",
		PasswordRequired: g.settings.ClientPassword != "",
		Capacity:         g.capacity(),
		FireCooldownMs:   g.settings.FireCooldownMs,
		EndReason:        g.endReason,
		Me:               meView{IsHost: isHost},
		Tanks:            []tankView{},
		Players:          []playerView{},
		Queue:            []playerView{},
	}
	if g.phase == phaseCountdown || g.phase == phasePlaying {
		s.RemainingMs = max(0, time.Until(g.phaseEndsAt).Milliseconds())
	}
	if isHost {
		st := g.settings
		s.Settings = &st
	}

	// Tanks: every connected one, plus any chosen tank that is offline.
	online := map[string]bool{}
	for _, t := range g.tanks.list() {
		online[t.ID] = true
		g.tankNames[t.ID] = t.Name
		tv := tankView{ID: t.ID, Name: t.Name, Team: defaultTeam(t.Name), Online: true}
		if p := g.tankOwner(t.ID); p != nil {
			tv.Player = p.name
		}
		s.Tanks = append(s.Tanks, tv)
	}
	for _, p := range g.players {
		if p.tank != "" && !online[p.tank] && (p.status == stLobby || p.status == stPlaying) {
			name := g.tankNames[p.tank]
			s.Tanks = append(s.Tanks, tankView{ID: p.tank, Name: name, Team: defaultTeam(name), Player: p.name})
		}
	}
	sort.Slice(s.Tanks, func(i, j int) bool { return naturalLess(s.Tanks[i].Name, s.Tanks[j].Name) })

	view := func(p *player) playerView {
		v := playerView{Name: p.name, Status: string(p.status), Tank: g.tankNames[p.tank], Online: p.clients > 0, Host: p.isHost}
		if isHost {
			v.Device = p.device
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
		s.Me.Team = defaultTeam(s.Me.TankName)
		for i, d := range g.queue {
			if d == device {
				s.Me.QueuePos = i + 1
			}
		}
	}
	if g.phase == phaseEnded || s.Me.Status == string(stFinished) {
		s.Results = g.results
	}
	return s
}

func (g *game) sortedPlayers() []*player {
	ps := make([]*player, 0, len(g.players))
	for _, p := range g.players {
		ps = append(ps, p)
	}
	sort.Slice(ps, func(i, j int) bool { return strings.ToLower(ps[i].name) < strings.ToLower(ps[j].name) })
	return ps
}

// broadcast sends every open page its snapshot. Caller holds g.mu.
func (g *game) broadcast() {
	cache := map[string][]byte{}
	for c := range g.clients {
		b, ok := cache[c.device]
		if !ok {
			b, _ = json.Marshal(g.snapshot(c.device))
			cache[c.device] = b
		}
		c.push(b)
	}
}

func (g *game) sendTo(device string, msg any) {
	b, _ := json.Marshal(msg)
	for c := range g.clients {
		if c.device == device {
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
		if c.device == device {
			n++
		}
	}
	return n
}

// ── Helpers ──────────────────────────────────────────────────────────────────

// defaultTeam is a tank's team from its name: "Desert-01" → Desert Squad.
func defaultTeam(tankName string) string {
	n := strings.ToLower(tankName)
	switch {
	case strings.HasPrefix(n, "desert"):
		return "Desert Squad"
	case strings.HasPrefix(n, "jungle"):
		return "Jungle Squad"
	}
	return ""
}

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
