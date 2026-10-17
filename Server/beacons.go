package main

// Capture-point beacons: the LED posts at North, Centre and South. They find
// the server the same way tanks do (UDP discovery, kind "beacon"), connect to
// /beacon and say hello with their point number (0 North, 1 Centre, 2 South).
// The server only sends them lighting commands; the firmware is unchanged
// from the Unity version:
//
//	set_color r g b       instant colour (0,0,0 = unlit)
//	capture_ripple r g b  animated capture in a team's colour
//	vp_ripple             one white ripple when that point scores
//	beacon_idle           white "waiting" bounce (no game running)

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"sync"
	"time"

	"github.com/coder/websocket"
)

// Team colours on the beacon LEDs (as in Unity).
var beaconColors = [2][3]int{{128, 55, 0}, {15, 80, 8}}

type beaconSession struct {
	point int
	id    string
	ip    string
	out   chan string
	stop  context.CancelFunc // closes the connection (a newer one replaced it)
}

type beaconManager struct {
	mu      sync.Mutex
	points  map[int]*beaconSession
	onHello func(point int) // the game resends the point's colour
}

func newBeaconManager() *beaconManager {
	return &beaconManager{points: map[int]*beaconSession{}}
}

// send queues a command for a point's beacon; dropped if none is connected
// (or it is far behind).
func (bm *beaconManager) send(point int, msg string) {
	if bm == nil {
		return
	}
	bm.mu.Lock()
	s := bm.points[point]
	bm.mu.Unlock()
	if s == nil {
		return
	}
	select {
	case s.out <- msg:
	default:
	}
}

// connected lists which points have a beacon online.
func (bm *beaconManager) connected() [3]bool {
	var out [3]bool
	if bm == nil {
		return out
	}
	bm.mu.Lock()
	defer bm.mu.Unlock()
	for p := range bm.points {
		if p >= 0 && p < 3 {
			out[p] = true
		}
	}
	return out
}

func (bm *beaconManager) handle(w http.ResponseWriter, r *http.Request) {
	c, err := websocket.Accept(w, r, &websocket.AcceptOptions{InsecureSkipVerify: true})
	if err != nil {
		return
	}
	c.SetReadLimit(4096)
	defer c.CloseNow()

	ctx, stop := context.WithCancel(r.Context())
	defer stop()
	var sess *beaconSession
	defer func() {
		if sess == nil {
			return
		}
		bm.mu.Lock()
		if bm.points[sess.point] == sess {
			delete(bm.points, sess.point)
		}
		bm.mu.Unlock()
		log.Printf("[Beacons] %s point beacon disconnected", pointNames[sess.point])
	}()

	for {
		readCtx, cancel := context.WithTimeout(ctx, 15*time.Second)
		typ, data, err := c.Read(readCtx)
		cancel()
		if err != nil {
			return
		}
		if typ != websocket.MessageText {
			continue
		}
		var msg struct {
			Cmd   string `json:"cmd"`
			ID    string `json:"id"`
			IP    string `json:"ip"`
			Point int    `json:"point"`
		}
		if json.Unmarshal(data, &msg) != nil || msg.Cmd != "hello" || sess != nil {
			continue
		}
		if msg.Point < 0 || msg.Point > 2 {
			log.Printf("[Beacons] hello with bad point %d from %s", msg.Point, r.RemoteAddr)
			continue
		}
		sess = &beaconSession{point: msg.Point, id: msg.ID, ip: msg.IP, out: make(chan string, 16), stop: stop}
		bm.mu.Lock()
		old := bm.points[msg.Point]
		bm.points[msg.Point] = sess
		bm.mu.Unlock()
		if old != nil {
			old.stop()
		}
		log.Printf("[Beacons] %s point beacon connected (%s, %s)", pointNames[msg.Point], msg.ID, msg.IP)
		go func(s *beaconSession) {
			for {
				var m string
				select {
				case <-ctx.Done():
					return
				case m = <-s.out:
				}
				wctx, cancel := context.WithTimeout(ctx, 3*time.Second)
				err := c.Write(wctx, websocket.MessageText, []byte(m))
				cancel()
				if err != nil {
					stop()
					return
				}
			}
		}(sess)
		if bm.onHello != nil {
			bm.onHello(msg.Point)
		}
	}
}

func colorCmd(cmd string, team int) string {
	r, g, b := 0, 0, 0
	if team == 0 || team == 1 {
		r, g, b = beaconColors[team][0], beaconColors[team][1], beaconColors[team][2]
	}
	return fmt.Sprintf(`{"cmd":"%s","r":%d,"g":%d,"b":%d}`, cmd, r, g, b)
}

// ── What the game tells the beacons (game.mu held) ──────────────────────────

// beaconShowOwner lights a point in its owner's colour (unlit if nobody).
func (g *game) beaconShowOwner(point, team int, animate bool) {
	if animate && team >= 0 {
		g.beacons.send(point, colorCmd("capture_ripple", team))
		return
	}
	g.beacons.send(point, colorCmd("set_color", team))
}

func (g *game) beaconVpRipple(point int) {
	g.beacons.send(point, `{"cmd":"vp_ripple"}`)
}

// beaconsForPhase sets every beacon for the game phase: owner colours (or
// unlit) while playing, the white idle bounce otherwise.
func (g *game) beaconsForPhase() {
	for p := 0; p < 3; p++ {
		g.beaconSync(p)
	}
}

func (g *game) beaconSync(point int) {
	if g.phase == phasePlaying && g.match != nil {
		g.beaconShowOwner(point, g.match.cpOwners[point], false)
		return
	}
	g.beacons.send(point, `{"cmd":"beacon_idle"}`)
}

// beaconConnected is called when a beacon says hello.
func (g *game) beaconConnected(point int) {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.beaconSync(point)
	g.broadcast()
}
