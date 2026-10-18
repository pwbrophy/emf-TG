package main

// Capture-point beacons: the LED posts. There are three, built as North,
// Centre and South: each says hello with its number (0, 1, 2) after finding
// the server the same way tanks do (UDP discovery, kind "beacon") and
// connecting to /beacon. The host chooses which capture point each one shows
// (CapturePoint.Beacon = number + 1).
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
	"math"
	"net/http"
	"strconv"
	"sync"
	"time"

	"github.com/coder/websocket"
)

var beaconNames = [3]string{"North", "Centre", "South"}

// ledColor turns a screen colour ("#rrggbb") into beacon LED levels: gamma
// corrected (LEDs look much brighter than screens at low levels) and capped
// at 128 like the Unity version's colours.
func ledColor(hex string) (r, g, b int) {
	if len(hex) != 7 {
		return 0, 0, 0
	}
	v, err := strconv.ParseUint(hex[1:], 16, 32)
	if err != nil {
		return 0, 0, 0
	}
	ch := [3]float64{float64(v >> 16 & 255), float64(v >> 8 & 255), float64(v & 255)}
	var out [3]float64
	top := 0.0
	for i, c := range ch {
		out[i] = math.Pow(c/255, 2.2)
		top = max(top, out[i])
	}
	if top == 0 {
		return 0, 0, 0
	}
	scale := func(x float64) int { return int(math.Round(x / top * 128)) }
	return scale(out[0]), scale(out[1]), scale(out[2])
}

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
		log.Printf("[Beacons] %s point beacon disconnected", beaconNames[sess.point])
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
		log.Printf("[Beacons] %s point beacon connected (%s, %s)", beaconNames[msg.Point], msg.ID, msg.IP)
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

func colorCmd(cmd string, r, g, b int) string {
	return fmt.Sprintf(`{"cmd":"%s","r":%d,"g":%d,"b":%d}`, cmd, r, g, b)
}

// ── What the game tells the beacons (game.mu held) ──────────────────────────

// beaconOf is the beacon (0-2) showing a capture point, -1 if none.
func (g *game) beaconOf(point int) int {
	if point < 0 || point >= len(g.settings.CapturePoints) {
		return -1
	}
	return g.settings.CapturePoints[point].Beacon - 1
}

// beaconShowOwner lights a point's beacon in its owner's colour (unlit if
// nobody).
func (g *game) beaconShowOwner(point, alliance int, animate bool) {
	b := g.beaconOf(point)
	if b < 0 {
		return
	}
	r, gr, bl := 0, 0, 0
	if alliance >= 0 && alliance < len(g.settings.Alliances) {
		r, gr, bl = ledColor(g.settings.Alliances[alliance].Color)
	}
	if animate && alliance >= 0 {
		g.beacons.send(b, colorCmd("capture_ripple", r, gr, bl))
		return
	}
	g.beacons.send(b, colorCmd("set_color", r, gr, bl))
}

func (g *game) beaconVpRipple(point int) {
	if b := g.beaconOf(point); b >= 0 {
		g.beacons.send(b, `{"cmd":"vp_ripple"}`)
	}
}

// beaconsForPhase sets every beacon for the game phase: owner colours (or
// unlit) while playing, the white idle bounce otherwise.
func (g *game) beaconsForPhase() {
	for b := 0; b < 3; b++ {
		g.beaconSync(b)
	}
}

// beaconSync sets one beacon (0-2) to what it should show now.
func (g *game) beaconSync(beacon int) {
	if g.phase == phasePlaying && g.match != nil {
		for i, cp := range g.settings.CapturePoints {
			if cp.Beacon-1 == beacon && i < len(g.match.cpOwners) {
				g.beaconShowOwner(i, g.match.cpOwners[i], false)
				return
			}
		}
		g.beacons.send(beacon, colorCmd("set_color", 0, 0, 0)) // not used in this game
		return
	}
	g.beacons.send(beacon, `{"cmd":"beacon_idle"}`)
}

// beaconConnected is called when a beacon says hello.
func (g *game) beaconConnected(beacon int) {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.beaconSync(beacon)
	g.broadcast()
}
