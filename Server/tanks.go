package main

// Tank connections, speaking the same protocol as the Unity server so the
// existing tank firmware works unchanged:
//
//  1. Discovery: a tank with no server broadcasts {"robotId":"<MAC12>",...}
//     to UDP 30560 every 2 s. We reply {"ws":"ws://<our-ip>:<port>/esp32"}.
//  2. Control: the tank opens that WebSocket and sends a hello with its id,
//     name, IP and the shared token. It then sends a heartbeat every 2 s.
//  3. Video: once we send stream_on, the tank's camera serves MJPEG at
//     http://<tank-ip>:81/stream. We pull that one stream into the relay,
//     which fans it out to every viewer. The tank's camera server can only
//     serve one client at a time, so the relay must be that single client.
//
// For now only video is wired up: no driving, firing or game state. Motors
// stay off (the firmware boots and reconnects with them disabled).

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"

	"thundergeddon/server/internal/mjpeg"
)

const (
	// A tank that sends nothing (it heartbeats every 2 s) for this long is gone.
	tankSilenceTimeout = 8 * time.Second
	// A streaming tank that delivers no frame for this long has stalled; the
	// video connection is closed and reopened.
	videoStallTimeout = 5 * time.Second
	// Tanks only send small flat JSON; anything bigger is a stranger.
	maxTankMessage = 4096
)

// videoURL is where a tank serves its camera. A variable so tests can point
// it at a fake camera.
var videoURL = func(ip string) string { return "http://" + ip + ":81/stream" }

var videoClient = &http.Client{Transport: &http.Transport{
	DialContext:           (&net.Dialer{Timeout: 5 * time.Second}).DialContext,
	ResponseHeaderTimeout: 5 * time.Second,
	DisableCompression:    true,
}}

type tankManager struct {
	rl       *relay
	token    string // shared hello token; "" = accept any tank
	httpPort int    // advertised in discovery replies

	mu    sync.Mutex
	tanks map[string]*tankSession // by robot id

	// Told when tanks come and go. Set before the server starts.
	onChange    func()          // the set of connected tanks changed
	onConnected func(id string) // a tank (re)connected and is ready for commands
}

type tankSession struct {
	id, name, ip string
	conn         *websocket.Conn // nil for a fake tank (-fake-tanks)
	connectedAt  time.Time
	lastSeen     atomic.Int64 // unix nanoseconds
	heap         atomic.Int64 // free heap bytes from the latest heartbeat
	stop         context.CancelFunc

	// Commands queued for the tank. Sending never blocks the caller (the game
	// holds its lock while sending); a writer goroutine drains this.
	out chan []byte
}

// tankInfo is what the game sees of a connected tank.
type tankInfo struct {
	ID, Name string
}

func newTankManager(rl *relay, tokenFile string, httpPort int) *tankManager {
	tm := &tankManager{rl: rl, httpPort: httpPort, tanks: map[string]*tankSession{}}
	if b, err := os.ReadFile(tokenFile); err == nil && strings.TrimSpace(string(b)) != "" {
		tm.token = strings.TrimSpace(string(b))
		log.Printf("[Tanks] hello token loaded from %s; tanks without it are refused", tokenFile)
	} else {
		log.Printf("[Tanks] WARNING: no token at %s; accepting ANY tank", tokenFile)
	}
	return tm
}

// ── Discovery ────────────────────────────────────────────────────────────────

func (tm *tankManager) runDiscovery(ctx context.Context, port int) {
	pc, err := net.ListenPacket("udp4", fmt.Sprintf(":%d", port))
	if err != nil {
		log.Printf("[Discovery] can't listen on UDP %d (is Unity running?): %v", port, err)
		return
	}
	go func() { <-ctx.Done(); pc.Close() }()
	log.Printf("[Discovery] listening on UDP %d", port)

	replied := map[string]bool{}
	buf := make([]byte, 512)
	for {
		n, addr, err := pc.ReadFrom(buf)
		if err != nil {
			if ctx.Err() == nil {
				log.Printf("[Discovery] stopped: %v", err)
			}
			return
		}
		var msg struct {
			RobotID string `json:"robotId"`
			Kind    string `json:"kind"`
		}
		if json.Unmarshal(buf[:n], &msg) != nil || msg.RobotID == "" {
			continue
		}
		if msg.Kind == "beacon" {
			continue // capture-point beacons aren't handled yet
		}
		ua, ok := addr.(*net.UDPAddr)
		if !ok {
			continue
		}
		local := localIPFacing(ua.IP)
		if local == "" {
			continue
		}
		reply := fmt.Sprintf(`{"ws":"ws://%s:%d/esp32"}`, local, tm.httpPort)
		_, _ = pc.WriteTo([]byte(reply), addr)
		if !replied[msg.RobotID] {
			replied[msg.RobotID] = true
			log.Printf("[Discovery] tank %s at %s -> %s", msg.RobotID, ua.IP, reply)
		}
	}
}

// localIPFacing returns this machine's address on the network that reaches
// ip (the router's LAN address, not its internet-side one).
func localIPFacing(ip net.IP) string {
	c, err := net.Dial("udp4", net.JoinHostPort(ip.String(), "9"))
	if err != nil {
		return ""
	}
	defer c.Close()
	return c.LocalAddr().(*net.UDPAddr).IP.String()
}

// ── Control connection ───────────────────────────────────────────────────────

// handleControl is the tank's WebSocket: GET /esp32.
func (tm *tankManager) handleControl(w http.ResponseWriter, r *http.Request) {
	c, err := websocket.Accept(w, r, &websocket.AcceptOptions{InsecureSkipVerify: true})
	if err != nil {
		return
	}
	c.SetReadLimit(maxTankMessage)
	defer c.CloseNow()

	var sess *tankSession
	defer func() {
		if sess != nil {
			tm.remove(sess)
		}
	}()

	for {
		readCtx, cancel := context.WithTimeout(r.Context(), tankSilenceTimeout)
		typ, data, err := c.Read(readCtx)
		cancel()
		if err != nil {
			if sess != nil {
				log.Printf("[Tanks] %s disconnected: %v", sess.label(), quietErr(err))
			}
			return
		}
		if sess != nil {
			sess.lastSeen.Store(time.Now().UnixNano())
		}
		if typ != websocket.MessageText {
			continue // old firmware could send camera frames here; not used
		}

		var msg struct {
			Cmd  string `json:"cmd"`
			ID   string `json:"id"`
			Tok  string `json:"tok"`
			Name string `json:"name"`
			IP   string `json:"ip"`
			Heap int64  `json:"heap"`
			// Camera settings the tank has saved (hello only).
			FPS      *int `json:"fps"`
			FSize    *int `json:"fsize"`
			FQuality *int `json:"fquality"`
		}
		if json.Unmarshal(data, &msg) != nil {
			continue
		}

		switch msg.Cmd {
		case "hello":
			if tm.token != "" && msg.Tok != tm.token {
				log.Printf("[Tanks] hello refused: wrong token (id=%s from %s)", msg.ID, r.RemoteAddr)
				continue
			}
			if msg.ID == "" {
				continue
			}
			ip := msg.IP
			if ip == "" {
				ip, _, _ = net.SplitHostPort(r.RemoteAddr)
			}
			sess = tm.add(msg.ID, msg.Name, ip, c)
			if msg.FPS != nil && msg.FSize != nil && msg.FQuality != nil {
				log.Printf("[Tanks] %s camera settings: max %d fps, frame size %d, JPEG quality %d",
					sess.label(), *msg.FPS, *msg.FSize, *msg.FQuality)
			}
			tm.announce(sess)
		case "hb":
			if sess != nil && msg.Heap > 0 {
				sess.heap.Store(msg.Heap)
			}
		}
	}
}

// add registers a tank's session, replacing any older session of the same
// tank (it reconnected), and starts pulling its video.
func (tm *tankManager) add(id, name, ip string, c *websocket.Conn) *tankSession {
	ctx, stop := context.WithCancel(context.Background())
	sess := &tankSession{id: id, name: name, ip: ip, conn: c, connectedAt: time.Now(), stop: stop,
		out: make(chan []byte, 64)}
	sess.lastSeen.Store(time.Now().UnixNano())

	tm.mu.Lock()
	old := tm.tanks[id]
	tm.tanks[id] = sess
	tm.mu.Unlock()

	if old != nil {
		old.stop()
		if old.conn != nil {
			old.conn.CloseNow()
		}
	}
	log.Printf("[Tanks] %s connected from %s", sess.label(), ip)

	s := tm.rl.get(id)
	s.setLabel(sess.label())
	go sess.writeLoop(ctx)
	if c != nil {
		go sess.pullVideo(ctx, s)
	}
	return sess
}

// addFake registers a pretend tank for testing without hardware
// (-fake-tanks). Its commands are logged instead of sent; its video comes
// from whatever is pushed to /api/ingest/<id> (e.g. cmd/loadtest).
func (tm *tankManager) addFake(id, name string) {
	tm.announce(tm.add(id, name, "fake", nil))
}

// announce turns a newly registered tank's camera on and tells the game.
func (tm *tankManager) announce(sess *tankSession) {
	sess.send(`{"cmd":"stream_on"}`)
	if tm.onChange != nil {
		tm.onChange()
	}
	if tm.onConnected != nil {
		tm.onConnected(sess.id)
	}
}

func (tm *tankManager) remove(sess *tankSession) {
	sess.stop()
	tm.mu.Lock()
	removed := tm.tanks[sess.id] == sess
	if removed {
		delete(tm.tanks, sess.id)
	}
	tm.mu.Unlock()
	if removed && tm.onChange != nil {
		tm.onChange()
	}
}

// list returns the connected tanks.
func (tm *tankManager) list() []tankInfo {
	tm.mu.Lock()
	defer tm.mu.Unlock()
	out := make([]tankInfo, 0, len(tm.tanks))
	for _, s := range tm.tanks {
		out = append(out, tankInfo{ID: s.id, Name: s.label()})
	}
	return out
}

// sendTo queues a JSON command for a tank. False if the tank isn't connected.
func (tm *tankManager) sendTo(id, msg string) bool {
	tm.mu.Lock()
	s := tm.tanks[id]
	tm.mu.Unlock()
	if s == nil {
		return false
	}
	s.send(msg)
	return true
}

func (s *tankSession) label() string {
	if s.name != "" {
		return s.name
	}
	return s.id
}

// send queues a command without waiting. If the tank is so far behind that
// its queue is full, the command is dropped: drive commands repeat 20 times
// a second, and a tank that can't keep up is about to time out anyway.
func (s *tankSession) send(msg string) {
	select {
	case s.out <- []byte(msg):
	default:
		log.Printf("[Tanks] %s: command queue full, dropped %s", s.label(), msg)
	}
}

func (s *tankSession) writeLoop(ctx context.Context) {
	for {
		select {
		case <-ctx.Done():
			return
		case msg := <-s.out:
			if s.conn == nil {
				if !strings.Contains(string(msg), `"drive"`) && !strings.Contains(string(msg), `"turret"`) {
					log.Printf("[Tanks] fake %s <- %s", s.label(), msg)
				}
				continue
			}
			wctx, cancel := context.WithTimeout(ctx, 3*time.Second)
			err := s.conn.Write(wctx, websocket.MessageText, msg)
			cancel()
			if err != nil {
				log.Printf("[Tanks] %s: send failed: %v", s.label(), err)
				return
			}
		}
	}
}

// ── Video pull ───────────────────────────────────────────────────────────────

// pullVideo keeps one connection open to the tank's camera and feeds every
// frame into the relay, reconnecting after a stall or error, until the
// tank's control session ends.
func (s *tankSession) pullVideo(ctx context.Context, out *stream) {
	url := videoURL(s.ip)
	for ctx.Err() == nil {
		frames, err := s.pullOnce(ctx, url, out)
		if ctx.Err() != nil {
			return
		}
		log.Printf("[Video] %s: camera stream ended after %d frames (%v), reconnecting", s.label(), frames, quietErr(err))
		select {
		case <-ctx.Done():
			return
		case <-time.After(time.Second):
		}
	}
}

func (s *tankSession) pullOnce(ctx context.Context, url string, out *stream) (int, error) {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()

	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	resp, err := videoClient.Do(req)
	if err != nil {
		return 0, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return 0, fmt.Errorf("camera answered %s", resp.Status)
	}

	out.sources.Add(1)
	defer out.sources.Add(-1)

	// Close the connection if no frame arrives for a while (Wi-Fi stall).
	// Before the first frame allow longer: the camera may still be starting.
	stalled := atomic.Bool{}
	watchdog := time.AfterFunc(2*videoStallTimeout, func() { stalled.Store(true); cancel() })
	defer watchdog.Stop()

	frames := 0
	err = mjpeg.ReadFrames(resp.Body, maxFrameBytes, func(f mjpeg.Frame) error {
		watchdog.Reset(videoStallTimeout)
		frames++
		out.publish(f)
		return nil
	})
	if stalled.Load() {
		err = errors.New("no frames for a few seconds")
	} else if err == nil {
		err = errors.New("camera closed the stream")
	}
	return frames, err
}

// ── Status ───────────────────────────────────────────────────────────────────

type tankStatus struct {
	ID           string  `json:"id"`
	Name         string  `json:"name"`
	IP           string  `json:"ip"`
	ConnectedS   int     `json:"connectedS"`
	LastSeenAgoS float64 `json:"lastSeenAgoS"`
	FreeHeapKB   int64   `json:"freeHeapKB"`
}

// handleList reports the connected tanks: GET /api/tanks.
func (tm *tankManager) handleList(w http.ResponseWriter, r *http.Request) {
	tm.mu.Lock()
	list := make([]tankStatus, 0, len(tm.tanks))
	for _, s := range tm.tanks {
		list = append(list, tankStatus{
			ID:           s.id,
			Name:         s.name,
			IP:           s.ip,
			ConnectedS:   int(time.Since(s.connectedAt).Seconds()),
			LastSeenAgoS: round1(time.Since(time.Unix(0, s.lastSeen.Load())).Seconds()),
			FreeHeapKB:   s.heap.Load() / 1024,
		})
	}
	tm.mu.Unlock()
	sort.Slice(list, func(i, j int) bool { return list[i].Name+list[i].ID < list[j].Name+list[j].ID })
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(list)
}

// quietErr shortens the errors a normal disconnect produces.
func quietErr(err error) error {
	if err == nil || errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return errors.New("timed out or closed")
	}
	return err
}
