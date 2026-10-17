// Thundergeddon game server.
//
// A single self-contained program meant to run on the GL-MT3000 travel router
// (linux/arm64) as well as on a Windows PC for development. The web pages are
// embedded in the binary, so deploying is one file copy.
//
// For now it only serves the front-end (title screen, main menu, settings).
// It runs alongside the Unity server without touching it: different folder,
// different port (8088 — Unity uses 8080-8082, ASP.NET 5000).
package main

import (
	"context"
	"embed"
	"encoding/json"
	"errors"
	"flag"
	"io/fs"
	"log"
	"net/http"
	"os"
	"os/signal"
	"runtime"
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
	flag.Parse()

	log.SetFlags(log.Ldate | log.Ltime | log.Lmicroseconds)

	web, err := fs.Sub(webFiles, "web")
	if err != nil {
		log.Fatal(err)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/api/info", handleInfo)
	mux.Handle("/", noCacheHTML(http.FileServer(http.FS(web))))

	srv := &http.Server{
		Addr:              *addr,
		Handler:           logRequests(mux),
		ReadHeaderTimeout: 10 * time.Second,
	}

	// procd (the router's service manager) stops services with SIGTERM.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	go func() {
		log.Printf("Thundergeddon server %s (%s/%s) listening on %s", version, runtime.GOOS, runtime.GOARCH, *addr)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatal(err)
		}
	}()

	<-ctx.Done()
	log.Print("Shutting down")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = srv.Shutdown(shutdownCtx)
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
		if r.URL.Path == "/" || strings.HasSuffix(r.URL.Path, ".html") {
			w.Header().Set("Cache-Control", "no-cache")
		}
		next.ServeHTTP(w, r)
	})
}

func logRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		log.Printf("%s %s %s (%s)", r.RemoteAddr, r.Method, r.URL.Path, time.Since(start).Round(time.Microsecond))
	})
}
