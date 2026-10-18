package main

// The lobby's helpers, as in the Unity version:
//
//   - Tank colours: once a player has a tank, its lights pulse a colour of its
//     own (set_player_color) and the player's phone says "Your tank is glowing
//     RED!", so they can find it on the table. Cleared when the game starts.
//   - Choosing an alliance: players pick a side and are given its first free
//     tank (all tanks are the same), which keeps the start simple.
//   - Cameras: a tank's camera only streams while it is needed: from the
//     countdown to game over for tanks in the game, never while paused.
//
// game.mu is held throughout.

import (
	"fmt"
	"log"
	"sort"
)

// The Unity version's ten colours: LED levels and the colour for screens.
var tankColors = []struct {
	name    string
	r, g, b int
	css     string
}{
	{"RED", 255, 0, 0, "#ff3b3b"},
	{"GREEN", 0, 200, 0, "#2fd52f"},
	{"BLUE", 0, 0, 255, "#4d7dff"},
	{"YELLOW", 255, 200, 0, "#ffc800"},
	{"CYAN", 0, 220, 255, "#00dcff"},
	{"PINK", 255, 0, 180, "#ff4fc8"},
	{"ORANGE", 255, 70, 0, "#ff7a2e"},
	{"PURPLE", 50, 0, 255, "#9a6bff"},
	{"LIME", 80, 255, 0, "#8cff3a"},
	{"WHITE", 255, 255, 255, "#ffffff"},
}

// colorWanted is the colour a tank should show now (-1 = none): in the lobby
// (and through the countdown) a tank with a player shows its colour.
func (g *game) colorWanted(id string) int {
	if g.phase != phaseLobby && g.phase != phaseCountdown {
		return -1
	}
	p := g.tankOwner(id)
	if p == nil {
		return -1
	}
	idx, ok := g.tankColor[id]
	if !ok {
		if g.phase != phaseLobby {
			return -1
		}
		idx = g.nextColor % len(tankColors)
		g.nextColor++
		g.tankColor[id] = idx
	}
	return idx
}

// lobbyColor is the colour name and screen colour a tank is showing.
func (g *game) lobbyColor(id string) (name, css string) {
	if i := g.colorShown[id] - 1; i >= 0 {
		return tankColors[i].name, tankColors[i].css
	}
	return "", ""
}

// syncTankColors tells each tank to show (or stop showing) its colour when
// that changed.
func (g *game) syncTankColors() {
	for _, t := range g.tanks.list() {
		want := g.colorWanted(t.ID) + 1
		if g.colorShown[t.ID] == want {
			continue
		}
		if want > 0 {
			c := tankColors[want-1]
			g.tanks.sendTo(t.ID, fmt.Sprintf(`{"cmd":"set_player_color","r":%d,"g":%d,"b":%d}`, c.r, c.g, c.b))
		} else {
			g.tanks.sendTo(t.ID, `{"cmd":"clear_player_color"}`)
		}
		g.colorShown[t.ID] = want
	}
}

// pickAlliance gives a lobby player the first free tank in an alliance
// ("" = give up their tank).
func (g *game) pickAlliance(c *client, allianceID string) {
	p := g.players[c.device]
	if p == nil || p.status != stLobby {
		return
	}
	if g.phase != phaseLobby {
		g.sendError(c, "Wait for the next game to choose.")
		return
	}
	if allianceID == "" {
		g.releaseTank(p)
		return
	}
	a := g.allianceIndex(allianceID)
	if a < 0 {
		return
	}
	if p.tank != "" && p.role != roleGunner && g.allianceOf(p.tank) == a {
		return // already in it
	}
	tanks := g.tanks.list()
	sort.Slice(tanks, func(i, j int) bool { return naturalLess(tanks[i].Name, tanks[j].Name) })
	for _, t := range tanks {
		if g.allianceOf(t.ID) == a && g.tankOwner(t.ID) == nil {
			g.releaseTank(p)
			p.tank = t.ID
			log.Printf("[Game] %s joined %s: %s", p.name, g.allianceName(a), t.Name)
			return
		}
	}
	g.sendError(c, "No free tanks in "+g.allianceName(a)+".")
}

// cameraWanted: should this tank's camera be streaming now?
func (g *game) cameraWanted(id string) bool {
	if g.settings.CamerasAlwaysOn {
		return true
	}
	if g.paused || g.match == nil || (g.phase != phaseCountdown && g.phase != phasePlaying) {
		return false
	}
	return g.match.fighters[id] != nil
}

// syncCameras switches every tank's camera on or off as needed.
func (g *game) syncCameras() {
	for _, t := range g.tanks.list() {
		g.tanks.setStreaming(t.ID, g.cameraWanted(t.ID))
	}
}
