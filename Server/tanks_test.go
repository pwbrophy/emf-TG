package main

// End-to-end test of the tank protocol with a simulated tank: discovery
// reply, hello with token, stream_on, and camera video reaching the relay.

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"

	"thundergeddon/server/internal/mjpeg"
)

// fakeCamera serves an MJPEG stream like the tank firmware's :81/stream.
func fakeCamera(t *testing.T) *httptest.Server {
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", mjpeg.ContentType)
		rc := http.NewResponseController(w)
		_ = mjpeg.WriteStart(w)
		for i := 0; ; i++ {
			if mjpeg.WriteFrame(w, mjpeg.Frame{Data: []byte(fmt.Sprintf("jpeg-%d", i))}) != nil || rc.Flush() != nil {
				return
			}
			select {
			case <-r.Context().Done():
				return
			case <-time.After(20 * time.Millisecond):
			}
		}
	}))
}

func newTestTankServer(t *testing.T, token string) (*tankManager, *relay, *httptest.Server) {
	t.Helper()
	tokenFile := filepath.Join(t.TempDir(), "ws_token.txt")
	if token != "" {
		if err := os.WriteFile(tokenFile, []byte(token+"\n"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	rl := newRelay()
	tm := newTankManager(rl, tokenFile, 8088)
	mux := http.NewServeMux()
	mux.HandleFunc("GET /esp32", tm.handleControl)
	srv := httptest.NewServer(mux)
	t.Cleanup(srv.Close)
	return tm, rl, srv
}

func dialTank(t *testing.T, srv *httptest.Server) *websocket.Conn {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	c, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(srv.URL, "http")+"/esp32", nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { c.CloseNow() })
	return c
}

func sendJSON(t *testing.T, c *websocket.Conn, v any) {
	t.Helper()
	b, _ := json.Marshal(v)
	if err := c.Write(context.Background(), websocket.MessageText, b); err != nil {
		t.Fatal(err)
	}
}

func TestTankHelloStartsVideo(t *testing.T) {
	cam := fakeCamera(t)
	defer cam.Close()
	old := videoURL
	videoURL = func(string) string { return cam.URL }
	defer func() { videoURL = old }()

	tm, rl, srv := newTestTankServer(t, "secret")
	c := dialTank(t, srv)
	sendJSON(t, c, map[string]any{"cmd": "hello", "id": "AABBCCDDEEFF", "tok": "secret", "name": "Desert-01", "ip": "127.0.0.1"})

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	_, msg, err := c.Read(ctx)
	if err != nil {
		t.Fatalf("no command after hello: %v", err)
	}
	if !strings.Contains(string(msg), `"stream_on"`) {
		t.Fatalf("expected stream_on, got %s", msg)
	}

	s := rl.get("AABBCCDDEEFF")
	deadline := time.Now().Add(3 * time.Second)
	for s.framesIn.Load() < 5 {
		if time.Now().After(deadline) {
			t.Fatalf("camera frames didn't reach the relay (got %d)", s.framesIn.Load())
		}
		time.Sleep(20 * time.Millisecond)
	}
	if got := s.getLabel(); got != "Desert-01" {
		t.Errorf("stream label = %q, want Desert-01", got)
	}

	tm.mu.Lock()
	n := len(tm.tanks)
	tm.mu.Unlock()
	if n != 1 {
		t.Errorf("tanks registered = %d, want 1", n)
	}

	// Disconnecting the tank stops its video and unregisters it.
	c.Close(websocket.StatusNormalClosure, "")
	deadline = time.Now().Add(3 * time.Second)
	for {
		tm.mu.Lock()
		n = len(tm.tanks)
		tm.mu.Unlock()
		if n == 0 && s.sources.Load() == 0 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("after disconnect: %d tanks, %d video sources", n, s.sources.Load())
		}
		time.Sleep(20 * time.Millisecond)
	}
}

func TestTankWrongTokenRefused(t *testing.T) {
	tm, _, srv := newTestTankServer(t, "secret")
	c := dialTank(t, srv)
	sendJSON(t, c, map[string]any{"cmd": "hello", "id": "AABBCCDDEEFF", "tok": "wrong", "ip": "127.0.0.1"})

	ctx, cancel := context.WithTimeout(context.Background(), 400*time.Millisecond)
	defer cancel()
	if _, msg, err := c.Read(ctx); err == nil {
		t.Fatalf("tank with the wrong token got a command: %s", msg)
	}
	tm.mu.Lock()
	defer tm.mu.Unlock()
	if len(tm.tanks) != 0 {
		t.Fatal("tank with the wrong token was registered")
	}
}

func TestDiscoveryReply(t *testing.T) {
	rl := newRelay()
	tm := newTankManager(rl, filepath.Join(t.TempDir(), "none"), 8088)

	// Find a free UDP port for the listener.
	probe, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	port := probe.LocalAddr().(*net.UDPAddr).Port
	probe.Close()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go tm.runDiscovery(ctx, fmt.Sprintf("127.0.0.1:%d", port)) // not all networks: no firewall prompt
	time.Sleep(100 * time.Millisecond)

	tank, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer tank.Close()
	to := &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: port}
	if _, err := tank.WriteTo([]byte(`{"robotId":"AABBCCDDEEFF","callsign":""}`), to); err != nil {
		t.Fatal(err)
	}

	_ = tank.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 256)
	n, _, err := tank.ReadFrom(buf)
	if err != nil {
		t.Fatalf("no discovery reply: %v", err)
	}
	if want := `{"ws":"ws://127.0.0.1:8088/esp32"}`; string(buf[:n]) != want {
		t.Fatalf("reply = %s, want %s", buf[:n], want)
	}
}
