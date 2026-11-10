package main

// Session logs: what a day of games leaves behind for working out what went
// wrong, kept small enough that the router's memory never fills.
//
//   - The hub log (thundergeddon-server.log): every line the hub prints. On
//     the router those lines also go to the system log, but that holds only
//     64 KB (about 700 lines, a few minutes of a game), so by the time a
//     problem is looked at they have gone.
//   - The event log (thundergeddon-events.jsonl): one JSON line per thing that
//     happened: games starting and ending (who played which tank, the rules,
//     the results), every shot with its timings and what each tank saw, kills,
//     respawns, captures, tanks and phones connecting and dropping, and every
//     30 s each tank's memory and the hub's own memory and processor use.
//   - The video log (videolog.go), as before.
//
// All three live in the temp folder (RAM on the router: gone after a reboot,
// kept over a hub restart or deploy). Each is capped (see the sizes below) and
// keeps one older file, so together they can never pass about 30 MB of the
// router's 240 MB. GET /api/logs.zip hands over the lot in one download
// (Server/fetch-logs.ps1 fetches and unpacks it).

import (
	"archive/zip"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"runtime/debug"
	"sync"
	"time"
)

const (
	serverLogMaxBytes = 3 << 20 // ×2 with the older file
	eventLogMaxBytes  = 4 << 20 // ×2 with the older file
	healthEvery       = 30 * time.Second
)

// rotFile is a file that is appended to and started afresh at maxBytes,
// keeping one older file (path + ".1"). Safe on a nil *rotFile (logging off).
type rotFile struct {
	mu   sync.Mutex
	path string
	max  int64
	f    *os.File
	size int64
}

// openRotFile carries on the file at path. Returns nil if it can't be written
// (or path is empty).
func openRotFile(path string, maxBytes int64) *rotFile {
	if path == "" {
		return nil
	}
	f, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		log.Printf("[Logs] can't write %s: %v", path, err)
		return nil
	}
	r := &rotFile{path: path, max: maxBytes, f: f}
	if st, err := f.Stat(); err == nil {
		r.size = st.Size()
	}
	return r
}

// Write appends p, which is one or more whole lines.
func (r *rotFile) Write(p []byte) (int, error) {
	if r == nil {
		return len(p), nil
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.f == nil {
		return len(p), nil
	}
	if r.size > 0 && r.size+int64(len(p)) > r.max {
		_ = r.f.Close()
		_ = os.Rename(r.path, r.path+".1")
		f, err := os.Create(r.path)
		if err != nil {
			r.f = nil // logging off rather than failing every write
			return len(p), nil
		}
		r.f, r.size = f, 0
	}
	n, _ := r.f.Write(p)
	r.size += int64(n)
	return len(p), nil // a full disk mustn't stop the hub printing
}

func (r *rotFile) close() {
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.f != nil {
		_ = r.f.Close()
		r.f = nil
	}
}

// files lists the current file and the older one, oldest first.
func (r *rotFile) files() []string {
	if r == nil {
		return nil
	}
	return []string{r.path + ".1", r.path}
}

// serve hands over the file (?old=1 for the older one).
func (r *rotFile) serve(w http.ResponseWriter, req *http.Request, name, contentType string) {
	if r == nil {
		http.Error(w, "this log is off", http.StatusNotFound)
		return
	}
	path := r.path
	if req.URL.Query().Get("old") != "" {
		path += ".1"
	}
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Content-Disposition", `attachment; filename="`+name+`"`)
	w.Header().Set("Cache-Control", "no-store")
	http.ServeFile(w, req, path)
}

// ── The hub log ──────────────────────────────────────────────────────────────

// startServerLog sends everything the hub prints to a capped file as well as
// to the terminal / system log, and keeps a crash's last words in a file of
// their own (path + ".crash"): a Go crash report bypasses the log, and on the
// router it would otherwise scroll out of the system log within minutes.
func startServerLog(path string) *rotFile {
	r := openRotFile(path, serverLogMaxBytes)
	if r == nil {
		return nil
	}
	log.SetOutput(io.MultiWriter(os.Stderr, r))
	crashPath := path + ".crash"
	// Keep the last crash (if any) where the bundle picks it up, then catch
	// the next one.
	if st, err := os.Stat(crashPath); err == nil && st.Size() > 0 {
		_ = os.Rename(crashPath, crashPath+".1")
		log.Printf("[Logs] the hub crashed last time it ran: report kept in %s.1", crashPath)
	}
	if f, err := os.Create(crashPath); err == nil {
		_ = debug.SetCrashOutput(f, debug.CrashOptions{})
		_ = f.Close() // SetCrashOutput keeps its own copy of the file
	}
	return r
}

// guard runs fn and, if it panics, logs the panic with its stack instead of
// letting it end the hub: a restart would drop every player, the host and the
// game in progress. The hub's own loops (clock, shots, stabiliser) run each
// step through this; a phone's or tank's message that panics only ends that
// connection (net/http does that).
func guard(what string, events *eventLog, fn func()) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[PANIC] in %s: %v\n%s", what, r, debug.Stack())
			events.add("panic", map[string]any{"where": what, "error": fmt.Sprint(r)})
		}
	}()
	fn()
}

// ── The event log ────────────────────────────────────────────────────────────

// eventLog writes one JSON object per line: {"t":"<hub time>","ev":"<kind>",
// ...fields}. Safe on a nil *eventLog (tests, logging off).
type eventLog struct {
	f *rotFile
}

func openEventLog(path string) *eventLog {
	f := openRotFile(path, eventLogMaxBytes)
	if f == nil {
		return nil
	}
	return &eventLog{f: f}
}

func (e *eventLog) add(kind string, fields map[string]any) {
	if e == nil {
		return
	}
	line := fmt.Sprintf(`{"t":%q,"ev":%q`, time.Now().Format("2006-01-02T15:04:05.000"), kind)
	if len(fields) > 0 {
		if b, err := json.Marshal(fields); err == nil && len(b) > 2 {
			line += "," + string(b[1:len(b)-1])
		}
	}
	_, _ = e.f.Write([]byte(line + "}\n"))
}

func (e *eventLog) close() {
	if e != nil {
		e.f.close()
	}
}

// shortDevice is enough of a phone's id to tell phones apart in the log
// without writing the whole id (which is what lets a phone be a player).
func shortDevice(d string) string {
	if len(d) > 6 {
		return d[:6]
	}
	return d
}

// ── The hub's own health ─────────────────────────────────────────────────────

// runHubHealth writes a "hub" event every 30 s: the hub's memory and
// processor use and how much room the temp folder has left. A slow leak or a
// filling disk over a day of games shows up here.
func runHubHealth(ctx context.Context, events *eventLog, tmpDir string, extra func() map[string]any) {
	if events == nil {
		return
	}
	sys := newSystemSampler()
	t := time.NewTicker(healthEvery)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
		}
		var ms runtime.MemStats
		runtime.ReadMemStats(&ms)
		f := map[string]any{
			"goroutines": runtime.NumGoroutine(),
			"go_heap_mb": round1(float64(ms.HeapAlloc) / (1 << 20)),
			"up_s":       int(time.Since(startedAt).Seconds()),
		}
		if st := sys.sample(); st != nil {
			f["rss_mb"], f["mem_avail_mb"] = st.RSSMB, st.MemAvailMB
			f["cpu_pct"], f["softirq_pct"], f["hub_cpu_pct"], f["load1"] = st.CPUBusyPct, st.CPUSoftirqPct, st.ProcCPUPct, st.Load1
		}
		if free, ok := diskFreeMB(tmpDir); ok {
			f["tmp_free_mb"] = free
		}
		if extra != nil {
			for k, v := range extra() {
				f[k] = v
			}
		}
		events.add("hub", f)
	}
}

// ── One download with everything ─────────────────────────────────────────────

// logBundle serves GET /api/logs.zip.
type logBundle struct {
	server *rotFile
	events *eventLog
	video  *videoLog
	extras func() map[string]any // small JSON files to add: name → value
}

func (b *logBundle) handle(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition",
		fmt.Sprintf(`attachment; filename="thundergeddon-logs-%s.zip"`, time.Now().Format("2006-01-02-150405")))
	w.Header().Set("Cache-Control", "no-store")
	zw := zip.NewWriter(w)
	defer zw.Close()

	create := func(name string) (io.Writer, error) {
		return zw.CreateHeader(&zip.FileHeader{Name: name, Method: zip.Deflate, Modified: time.Now()})
	}
	addFile := func(name, path string) {
		f, err := os.Open(path)
		if err != nil {
			return // no older file yet
		}
		defer f.Close()
		if out, err := create(name); err == nil {
			_, _ = io.Copy(out, f)
		}
	}
	if b.server != nil {
		addFile("server.log.1", b.server.path+".1")
		addFile("server.log", b.server.path)
		addFile("crash-previous.txt", b.server.path+".crash.1")
	}
	if b.events != nil {
		addFile("events.jsonl.1", b.events.f.path+".1")
		addFile("events.jsonl", b.events.f.path)
	}
	if b.video != nil {
		b.video.flush()
		addFile("video.csv.1", b.video.path+".1")
		addFile("video.csv", b.video.path)
	}
	if b.extras != nil {
		for name, v := range b.extras() {
			if out, err := create(name); err == nil {
				enc := json.NewEncoder(out)
				enc.SetIndent("", "  ")
				_ = enc.Encode(v)
			}
		}
	}
	// The router's own log (kernel and Wi-Fi driver lines: tanks joining and
	// dropping off the Wi-Fi). Only what its 64 KB holds at this moment.
	if runtime.GOOS == "linux" {
		ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
		defer cancel()
		if out, err := exec.CommandContext(ctx, "logread").Output(); err == nil {
			if f, err := create("router-system-log.txt"); err == nil {
				_, _ = f.Write(out)
			}
		}
	}
}

// sessionLogPaths: where the hub and event logs go, next to the video log
// (the temp folder), or dir if given.
func sessionLogPaths(dir string) (server, events string) {
	if dir == "" {
		dir = os.TempDir()
	}
	return filepath.Join(dir, "thundergeddon-server.log"), filepath.Join(dir, "thundergeddon-events.jsonl")
}
