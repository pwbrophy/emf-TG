// Thundergeddon game server.
//
// A single self-contained program meant to run on the GL-MT3000 travel router
// (linux/arm64) as well as on a Windows PC for development. The web pages are
// embedded in the binary, so deploying is one file copy.
//
// For now it serves the front-end (title screen, main menu, settings) and a
// video relay used by the fake-tank load test (cmd/loadtest). It runs alongside the Unity server without touching it: different folder,
// different port (8088 — Unity uses 8080-8082, ASP.NET 5000).
package main

import (
	"context"
	"embed"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io/fs"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"
)

// Set at build time: go build -ldflags "-X main.version=..."
var version = "dev"

//go:embed web
var webFiles embed.FS

var startedAt = time.Now()

func main() {
	addr := flag.String("addr", ":8088", "address to listen on")
	tokenFile := flag.String("token-file", "ws_token.txt", "file holding the tanks' shared hello token (same as Unity's ws_token.txt)")
	discoveryPort := flag.Int("discovery-port", 30560, "UDP port tanks broadcast on to find a server; 0 = don't answer")
	settingsFile := flag.String("settings", "settings.json", "where the host's settings are saved")
	fakeTanks := flag.Int("fake-tanks", 0, "add this many pretend tanks (ids 1..N) for testing without hardware; feed them video with cmd/loadtest")
	flag.Parse()

	log.SetFlags(log.Ldate | log.Ltime | log.Lmicroseconds)

	web, err := fs.Sub(webFiles, "web")
	if err != nil {
		log.Fatal(err)
	}

	_, portStr, err := net.SplitHostPort(*addr)
	if err != nil {
		log.Fatalf("bad -addr %q: %v", *addr, err)
	}
	httpPort, _ := strconv.Atoi(portStr)

	// procd (the router's service manager) stops services with SIGTERM.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	rl := newRelay()
	stats := startStats(rl)
	tanks := newTankManager(rl, *tokenFile, httpPort)
	g := newGame(tanks, *settingsFile)
	g.beacons = newBeaconManager()
	g.beacons.onHello = g.beaconConnected
	tanks.onChange = g.tanksChanged
	tanks.onConnected = g.tankConnected
	tanks.onReport = g.tankMessage
	go g.runClock(ctx.Done())
	go g.ir.run(ctx.Done())
	for i := 1; i <= *fakeTanks; i++ {
		team := "Desert"
		if i%2 == 0 {
			team = "Jungle"
		}
		tanks.addFake(strconv.Itoa(i), fmt.Sprintf("%s-%02d", team, i))
	}
	if *discoveryPort > 0 {
		go tanks.runDiscovery(ctx, fmt.Sprintf(":%d", *discoveryPort))
	}

	mux := http.NewServeMux()
	mux.HandleFunc("GET /ws", g.handleWS)
	mux.HandleFunc("/api/info", handleInfo)
	mux.HandleFunc("GET /api/stats", stats.handle)
	mux.HandleFunc("GET /api/tanks", tanks.handleList)
	mux.HandleFunc("GET /esp32", tanks.handleControl)
	mux.HandleFunc("GET /beacon", g.beacons.handle) // capture-point beacons (any path works for them; this is what discovery tells them)
	mux.HandleFunc("POST /api/ingest/{id}", rl.handleIngest)
	mux.HandleFunc("GET /api/stream/{id}", rl.handleView)
	mux.HandleFunc("GET /api/robot-stream", rl.handleViewByQuery) // addresses the in-game page uses
	mux.HandleFunc("GET /api/video-ws", rl.handleVideoWS)
	if *fakeTanks > 0 {
		mux.HandleFunc("POST /api/dev/rfid", tanks.handleFakeRfid) // pretend tag scans
	}
	mux.Handle("/", noCacheHTML(http.FileServer(http.FS(web))))

	srv := &http.Server{
		Addr:              *addr,
		Handler:           logRequests(mux),
		ReadHeaderTimeout: 10 * time.Second,
		// Requests share the shutdown signal, so long-lived video and tank
		// connections end at once on stop instead of holding the server up
		// until the service manager kills it.
		BaseContext: func(net.Listener) context.Context { return ctx },
	}

	go func() {
		log.Printf("Thundergeddon server %s (%s/%s) listening on %s", version, runtime.GOOS, runtime.GOARCH, *addr)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatal(err)
		}
	}()

	<-ctx.Done()
	log.Print("Shutting down")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if srv.Shutdown(shutdownCtx) != nil {
		_ = srv.Close()
	}
}

// handleInfo reports what is running and where, so the settings screen can
// show whether the page came from the PC or the router.
func handleInfo(w http.ResponseWriter, r *http.Request) {
	host, _ := os.Hostname()
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"version":  version,
		"os":       runtime.GOOS,
		"arch":     runtime.GOARCH,
		"hostname": host,
		"uptimeS":  int(time.Since(startedAt).Seconds()),
	})
}

// Phones otherwise cache the HTML heuristically and keep showing a stale page
// after a deploy. no-cache = always revalidate (a cheap 304 when unchanged).
func noCacheHTML(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/" || strings.HasSuffix(r.URL.Path, ".html") ||
			strings.HasSuffix(r.URL.Path, ".js") || strings.HasSuffix(r.URL.Path, ".css") {
			w.Header().Set("Cache-Control", "no-cache")
		}
		next.ServeHTTP(w, r)
	})
}

func logRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		// Status polls arrive every couple of seconds; logging them would
		// flood the router's small log buffer.
		switch r.URL.Path {
		case "/api/stats", "/api/info", "/api/tanks":
			return
		}
		log.Printf("%s %s %s (%s)", r.RemoteAddr, r.Method, r.URL.Path, time.Since(start).Round(time.Microsecond))
	})
}
