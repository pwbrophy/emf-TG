package main

// Video relay: fans each tank's camera stream out to every viewer.
//
// The tank sends its stream to the server once; each viewer (the player's
// phone, the spectator display) gets its own copy from the server. A tank
// therefore never serves more than one connection, however many people watch.
//
// Each viewer has a one-frame mailbox. If a viewer's connection is slower than
// the camera, older frames are replaced by newer ones instead of queueing up,
// so a slow phone sees a lower frame rate rather than ever-growing delay.
//
// Sources currently PUSH their stream (POST /api/ingest/{id}); that is what the
// fake-tank load test uses. Pulling from the real tanks' :81/stream comes later.

import (
	"context"
	"log"
	"net/http"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"

	"thundergeddon/server/internal/mjpeg"
)

// A tank camera frame is ~8-15 KB; anything much bigger is not a tank.
const maxFrameBytes = 512 * 1024

// A viewer whose connection makes no progress for this long is dropped.
// Long enough to ride out a Wi-Fi blip, short enough not to pile up dead
// connections when a phone just disappears.
const viewerWriteTimeout = 20 * time.Second

type relay struct {
	mu      sync.Mutex
	streams map[string]*stream
}

type stream struct {
	id       string
	mu       sync.Mutex
	label    string // display name (the tank's name); guarded by mu
	viewers  map[*viewer]struct{}
	sources  atomic.Int32
	framesIn atomic.Int64
	bytesIn  atomic.Int64
}

type viewer struct {
	who     string
	mailbox chan mjpeg.Frame // capacity 1: only ever the newest frame
	maxFPS  float64          // frame-rate cap; 0 = none

	// Frame-rate cap bookkeeping, touched only by the publishing goroutine.
	// credit grows by maxFPS per second; each frame passed on spends 1.
	// (A plain "at least 1/maxFPS since the last one" rule passes only every
	// other frame when the cap sits between the source rate and half of it.)
	credit   float64
	lastSeen time.Time

	sent    atomic.Int64
	dropped atomic.Int64 // replaced in the mailbox before the viewer took them
	skipped atomic.Int64 // left out on purpose by the frame-rate cap
}

func newRelay() *relay {
	return &relay{streams: map[string]*stream{}}
}

func (rl *relay) get(id string) *stream {
	rl.mu.Lock()
	defer rl.mu.Unlock()
	s := rl.streams[id]
	if s == nil {
		s = &stream{id: id, viewers: map[*viewer]struct{}{}}
		rl.streams[id] = s
	}
	return s
}

func (rl *relay) all() []*stream {
	rl.mu.Lock()
	defer rl.mu.Unlock()
	out := make([]*stream, 0, len(rl.streams))
	for _, s := range rl.streams {
		out = append(out, s)
	}
	return out
}

func (s *stream) publish(f mjpeg.Frame) {
	s.framesIn.Add(1)
	s.bytesIn.Add(int64(len(f.Data)))

	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	for v := range s.viewers {
		if v.maxFPS > 0 {
			v.credit = min(v.credit+now.Sub(v.lastSeen).Seconds()*v.maxFPS, 2)
			v.lastSeen = now
			if v.credit < 1 {
				v.skipped.Add(1)
				continue
			}
			v.credit--
		}
		select {
		case v.mailbox <- f:
		default:
			// Mailbox full: the viewer hasn't taken the last frame yet.
			// Swap it for this newer one.
			select {
			case <-v.mailbox:
				v.dropped.Add(1)
			default:
			}
			select {
			case v.mailbox <- f:
			default:
			}
		}
	}
}

func (s *stream) setLabel(l string) {
	s.mu.Lock()
	s.label = l
	s.mu.Unlock()
}

func (s *stream) getLabel() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.label == "" {
		return s.id
	}
	return s.label
}

func (s *stream) addViewer(v *viewer) {
	s.mu.Lock()
	s.viewers[v] = struct{}{}
	s.mu.Unlock()
}

func (s *stream) removeViewer(v *viewer) {
	s.mu.Lock()
	delete(s.viewers, v)
	s.mu.Unlock()
}

func (s *stream) viewerList() []*viewer {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]*viewer, 0, len(s.viewers))
	for v := range s.viewers {
		out = append(out, v)
	}
	return out
}

// handleIngest receives a source's MJPEG stream: POST /api/ingest/{id}.
func (rl *relay) handleIngest(w http.ResponseWriter, r *http.Request) {
	s := rl.get(r.PathValue("id"))
	s.sources.Add(1)
	defer s.sources.Add(-1)

	err := mjpeg.ReadFrames(r.Body, maxFrameBytes, func(f mjpeg.Frame) error {
		s.publish(f)
		return nil
	})
	if err != nil {
		log.Printf("[Video] stream %s: source error: %v", s.id, err)
		http.Error(w, err.Error(), http.StatusBadRequest)
	}
}

// handleView streams one tank's video to a viewer:
// GET /api/stream/{id}?who=<name>&maxfps=<n>
func (rl *relay) handleView(w http.ResponseWriter, r *http.Request) {
	rl.serveMJPEG(w, r, r.PathValue("id"))
}

// handleViewByQuery is the same stream at the address the in-game page
// (play.html, the Unity-era player page) uses: GET /api/robot-stream?url=<id>.
func (rl *relay) handleViewByQuery(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("url")
	if id == "" {
		http.Error(w, "missing url", http.StatusBadRequest)
		return
	}
	rl.serveMJPEG(w, r, id)
}

func newViewer(r *http.Request) *viewer {
	v := &viewer{who: viewerName(r.URL.Query().Get("who")), mailbox: make(chan mjpeg.Frame, 1)}
	if fps, _ := strconv.Atoi(r.URL.Query().Get("maxfps")); fps > 0 {
		v.maxFPS = float64(fps)
	}
	return v
}

// handleVideoWS sends each frame as one binary WebSocket message:
// GET /api/video-ws?url=<id>. iPhones (and Firefox) can't show MJPEG in an
// <img> reliably, so the in-game page draws these frames on a canvas.
func (rl *relay) handleVideoWS(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("url")
	if id == "" {
		http.Error(w, "missing url", http.StatusBadRequest)
		return
	}
	c, err := websocket.Accept(w, r, &websocket.AcceptOptions{CompressionMode: websocket.CompressionDisabled})
	if err != nil {
		return
	}
	defer c.CloseNow()
	// Nothing is read from the page, but reading notices it closing.
	ctx := c.CloseRead(r.Context())

	s := rl.get(id)
	v := newViewer(r)
	s.addViewer(v)
	defer s.removeViewer(v)

	for {
		select {
		case <-ctx.Done():
			return
		case f := <-v.mailbox:
			wctx, cancel := context.WithTimeout(ctx, viewerWriteTimeout)
			err := c.Write(wctx, websocket.MessageBinary, f.Data)
			cancel()
			if err != nil {
				return
			}
			v.sent.Add(1)
		}
	}
}

func (rl *relay) serveMJPEG(w http.ResponseWriter, r *http.Request, id string) {
	s := rl.get(id)
	v := newViewer(r)

	rc := http.NewResponseController(w)
	w.Header().Set("Content-Type", mjpeg.ContentType)
	w.Header().Set("Cache-Control", "no-cache, no-store")
	_ = rc.SetWriteDeadline(time.Now().Add(viewerWriteTimeout))
	if mjpeg.WriteStart(w) != nil || rc.Flush() != nil {
		return
	}

	s.addViewer(v)
	defer s.removeViewer(v)

	for {
		select {
		case <-r.Context().Done():
			return
		case f := <-v.mailbox:
			_ = rc.SetWriteDeadline(time.Now().Add(viewerWriteTimeout))
			if err := mjpeg.WriteFrame(w, f); err != nil {
				return
			}
			if err := rc.Flush(); err != nil {
				return
			}
			v.sent.Add(1)
		}
	}
}

// Label for a viewer in the stats: trimmed, printable, capped.
func viewerName(who string) string {
	clean := make([]rune, 0, len(who))
	for _, c := range who {
		if c >= ' ' && c != 0x7f {
			clean = append(clean, c)
		}
	}
	if len(clean) == 0 {
		return "viewer"
	}
	if len(clean) > 24 {
		clean = clean[:24]
	}
	return string(clean)
}
