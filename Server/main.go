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
	"path/filepath"
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
	tokenFile := flag.String("token-file", "ws_token.txt", "file holding the tanks' shared hello token")
	discoveryPort := flag.Int("discovery-port", 30560, "UDP port tanks broadcast on to find a server; 0 = don't answer")
	settingsFile := flag.String("settings", "settings.json", "where the host's settings are saved")
	fakeTanks := flag.Int("fake-tanks", 0, "add this many pretend tanks (ids 1..N) for testing without hardware; feed them video with cmd/loadtest")
	wifiIface := flag.String("wifi-iface", "ra0", "the router's 2.4 GHz Wi-Fi interface, for its view of each tank (iwinfo) and its airtime fairness (iwpriv); empty = off")
	videoLogFile := flag.String("video-log", filepath.Join(os.TempDir(), "thundergeddon-video.csv"),
		"CSV log of every tank's video and Wi-Fi signal, for chasing choppy feeds (download: /api/video-log); empty = off")
	sigtestDir := flag.String("sigtest-dir", "", "where finished signal test runs are kept (default: a sigtest folder next to the settings file)")
	webDir := flag.String("web-dir", "", "serve the pages from this folder instead of the built-in copy (for editing them without rebuilding)")
	logDir := flag.String("log-dir", "", "where the hub log and the event log are kept (default: the temp folder, RAM on the router); \"off\" = don't keep them")
	flag.Parse()

	log.SetFlags(log.Ldate | log.Ltime | log.Lmicroseconds)

	// Session logs (sessionlog.go): set up first, so everything below is in them.
	var serverLog *rotFile
	var events *eventLog
	if *logDir != "off" {
		serverPath, eventsPath := sessionLogPaths(*logDir)
		serverLog = startServerLog(serverPath)
		events = openEventLog(eventsPath)
		defer serverLog.close()
		defer events.close()
	}

	web, err := fs.Sub(webFiles, "web")
	if err != nil {
		log.Fatal(err)
	}
	if *webDir != "" {
		web = os.DirFS(*webDir)
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
	vlog := openVideoLog(*videoLogFile)
	defer vlog.close()
	stats := startStats(rl, vlog, startRouterLink(*wifiIface, rl), events)
	tanks := newTankManager(rl, *tokenFile, httpPort)
	tanks.vlog = vlog
	tanks.events = events
	g := newGame(tanks, *settingsFile)
	g.vlog = vlog
	g.events = events
	events.add("hub_start", map[string]any{"version": version, "os": runtime.GOOS + "/" + runtime.GOARCH})
	g.airtime = startAirtimeControl(*wifiIface, g.settings.AirtimeFairness)
	g.checkTanks = stats.check
	if *sigtestDir == "" {
		*sigtestDir = filepath.Join(filepath.Dir(*settingsFile), "sigtest")
	}
	// Runs used to be saved next to the video log (the router's RAM): keep them.
	g.sigtest = newSignalTest(tanks, rl, *wifiIface, *sigtestDir, filepath.Dir(*videoLogFile))
	tanks.fakeNoShots = g.sigtest.active
	tanks.noPings = g.sigtest.active // the signal test waits for pongs of its own
	g.sigtest.onDone = func() {
		g.mu.Lock()
		g.syncCameras() // back to the phase's cameras
		g.broadcast()
		g.mu.Unlock()
	}
	g.beacons = newBeaconManager()
	g.beacons.onHello = g.beaconConnected
	tanks.onChange = g.tanksChanged
	tanks.onConnected = g.tankConnected
	tanks.onReport = g.tankMessage
	go g.runClock(ctx.Done())
	go g.ir.run(ctx.Done())
	go g.runStabiliser(ctx.Done())
	go tanks.runHealth(ctx)
	go runHubHealth(ctx, events, os.TempDir(), g.healthCounts)
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
	mux.HandleFunc("GET /api/video-log", vlog.handle)
	mux.HandleFunc("GET /api/server-log", func(w http.ResponseWriter, r *http.Request) {
		serverLog.serve(w, r, "thundergeddon-server.log", "text/plain; charset=utf-8")
	})
	mux.HandleFunc("GET /api/events", func(w http.ResponseWriter, r *http.Request) {
		if events == nil {
			http.Error(w, "the event log is off", http.StatusNotFound)
			return
		}
		events.f.serve(w, r, "thundergeddon-events.jsonl", "application/x-ndjson")
	})
	bundle := &logBundle{server: serverLog, events: events, video: vlog, extras: func() map[string]any {
		return map[string]any{
			"info.json":     infoNow(),
			"stats.json":    stats.snapshot(),
			"tanks.json":    tanks.statusList(),
			"settings.json": g.settingsForLog(),
		}
	}}
	mux.HandleFunc("GET /api/logs.zip", bundle.handle)
	mux.HandleFunc("GET /api/sigtest", g.sigtest.handle)
	mux.HandleFunc("GET /api/sigtest.csv", g.sigtest.handleCSV)
	mux.HandleFunc("GET /api/sigtest/runs", g.sigtest.handleRuns)
	mux.HandleFunc("GET /api/sigtest/history", g.sigtest.handleHistory)
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
	// The link to give players (e.g. on a QR code): straight to Name Entry.
	mux.HandleFunc("GET /join", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/#join", http.StatusFound)
	})
	mux.Handle("/", noCacheHTML(http.FileServer(http.FS(web))))

	srv := &http.Server{
		Addr:              *addr,
		Handler:           logRequests(mux),
		ReadHeaderTimeout: 10 * time.Second,
		// A phone that walks off leaves its idle connection behind; without
		// this they are only cleared by TCP keep-alive, minutes later.
		IdleTimeout: 2 * time.Minute,
		// Requests share the shutdown signal, so long-lived video and tank
		// connections end at once on stop instead of holding the server up
		// until the service manager kills it.
		BaseContext: func(net.Listener) context.Context { return ctx },
		// Video handlers tune their own socket (limitSendQueue).
		ConnContext: func(c context.Context, conn net.Conn) context.Context {
			return context.WithValue(c, connKey{}, conn)
		},
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
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(infoNow())
}

func infoNow() map[string]any {
	host, _ := os.Hostname()
	return map[string]any{
		"version":  version,
		"os":       runtime.GOOS,
		"arch":     runtime.GOARCH,
		"hostname": host,
		"uptimeS":  int(time.Since(startedAt).Seconds()),
		// The hub's clock, which every log line uses: it can be wrong when
		// the router has no internet to set it from.
		"time": time.Now().Format("2006-01-02T15:04:05.000"),
	}
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
		case "/api/stats", "/api/info", "/api/tanks", "/api/sigtest", "/api/sigtest/history":
			return
		}
		log.Printf("%s %s %s (%s)", r.RemoteAddr, r.Method, r.URL.Path, time.Since(start).Round(time.Microsecond))
	})
}
