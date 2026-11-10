package main

import (
	"archive/zip"
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestRotFileKeepsOneOlderFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "x.log")
	r := openRotFile(path, 100)
	line := []byte(strings.Repeat("a", 39) + "\n") // 40 bytes
	for i := 0; i < 10; i++ {
		_, _ = r.Write(line)
	}
	r.close()
	cur, _ := os.ReadFile(path)
	old, _ := os.ReadFile(path + ".1")
	if len(cur) == 0 || len(cur) > 100 || len(old) == 0 || len(old) > 100 {
		t.Fatalf("sizes: current %d, older %d; want both between 1 and 100", len(cur), len(old))
	}
	if entries, _ := os.ReadDir(filepath.Dir(path)); len(entries) != 2 {
		t.Fatalf("%d files kept, want 2 (never more, however much is written)", len(entries))
	}

	// Carries on an existing file rather than starting it afresh.
	r = openRotFile(path, 100)
	_, _ = r.Write([]byte("b\n"))
	r.close()
	again, _ := os.ReadFile(path)
	if !bytes.HasPrefix(again, cur) || !bytes.HasSuffix(again, []byte("b\n")) {
		t.Fatalf("reopening didn't append: %q then %q", cur, again)
	}

	// Off (nil) is safe everywhere.
	var off *rotFile
	_, _ = off.Write([]byte("x"))
	off.close()
	var noEvents *eventLog
	noEvents.add("x", map[string]any{"a": 1})
	noEvents.close()
}

// readEvents parses an event log file.
func readEvents(t *testing.T, path string) []map[string]any {
	t.Helper()
	f, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	var out []map[string]any
	sc := bufio.NewScanner(f)
	sc.Buffer(nil, 1<<20)
	for sc.Scan() {
		var m map[string]any
		if err := json.Unmarshal(sc.Bytes(), &m); err != nil {
			t.Fatalf("bad event line %q: %v", sc.Text(), err)
		}
		out = append(out, m)
	}
	return out
}

func eventsOf(evs []map[string]any, kind string) []map[string]any {
	var out []map[string]any
	for _, e := range evs {
		if e["ev"] == kind {
			out = append(out, e)
		}
	}
	return out
}

func TestEventLogLines(t *testing.T) {
	path := filepath.Join(t.TempDir(), "events.jsonl")
	e := openEventLog(path)
	e.add("plain", nil)
	e.add("shot", map[string]any{"tank": `Desert "01"`, "ms": 12, "saw": map[string]int{"Jungle-06": 3}})
	e.close()
	evs := readEvents(t, path)
	if len(evs) != 2 || evs[0]["ev"] != "plain" || evs[1]["tank"] != `Desert "01"` || evs[1]["ms"] != 12.0 {
		t.Fatalf("events: %v", evs)
	}
	if _, err := time.Parse("2006-01-02T15:04:05.000", evs[0]["t"].(string)); err != nil {
		t.Fatalf("time %v: %v", evs[0]["t"], err)
	}
}

func TestGuardKeepsTheHubUp(t *testing.T) {
	path := filepath.Join(t.TempDir(), "events.jsonl")
	e := openEventLog(path)
	ran := false
	guard("a test", e, func() {
		var m map[string]int
		m["x"] = 1 // panics
	})
	guard("a test", e, func() { ran = true })
	e.close()
	if !ran {
		t.Fatal("guard didn't carry on after a panic")
	}
	if p := eventsOf(readEvents(t, path), "panic"); len(p) != 1 || p[0]["where"] != "a test" {
		t.Fatalf("panic events: %v", p)
	}
}

func TestTankHealthKeepsTheLowestFigures(t *testing.T) {
	var h tankHealth
	if h.take() != nil {
		t.Fatal("figures before any heartbeat")
	}
	now := time.Now()
	rssi1, rssi2, loop1, loop2 := -50, -71, 4, 220
	temp := 41.5
	h.heartbeat(now, 200<<10, 100<<10, 60_000, &rssi1, &temp, nil, &loop1, nil, nil, nil)
	h.heartbeat(now.Add(2*time.Second), 150<<10, 60<<10, 62_000, &rssi2, &temp, nil, &loop2, nil, nil, nil)
	h.heartbeat(now.Add(9*time.Second), 180<<10, 90<<10, 69_000, &rssi1, &temp, nil, &loop1, nil, nil, nil)
	f := h.take()
	want := map[string]any{"heap_kb": int64(180), "heap_low_kb": int64(150), "blk_kb": int64(90), "blk_low_kb": int64(60),
		"rssi_low": -71, "loop_max_ms": 220, "hb": 3, "hb_gap_max_ms": int64(7000), "up_s": int64(69)}
	for k, v := range want {
		if f[k] != v {
			t.Errorf("%s = %v (%T), want %v", k, f[k], f[k], v)
		}
	}
	if h.take() != nil {
		t.Fatal("take didn't start a new period")
	}
	// Old firmware: no loop figure, and it isn't made up.
	h.heartbeat(now, 1, 1, 1, nil, nil, nil, nil, nil, nil, nil)
	if f := h.take(); f["loop_max_ms"] != nil || f["rssi_low"] != nil {
		t.Fatalf("figures the tank didn't send: %v", f)
	}
}

// A tank that isn't connected (flat battery, restarting) must not hold up
// every shot at its alliance for the whole answer timeout.
func TestShotDoesNotWaitForATankThatIsOff(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01", "Jungle-02", "Jungle-03")
	evPath := filepath.Join(t.TempDir(), "events.jsonl")
	g.events = openEventLog(evPath)
	g.settings.IrWindowMs = 5
	g.settings.IrAckTimeoutMs = 100
	g.settings.IrWindowTimeoutMs = 2000 // the wait an absent tank used to cost, twice
	g.settings.FireCooldownMs = 250
	ft.onSend = func(id, msg string) {
		switch {
		case strings.Contains(msg, `"ir_emit_left"`):
			go g.tankMessage(id, tankReport{Cmd: "ir_emit_ack"})
		case strings.Contains(msg, `"ir_listen_window"`):
			mask := 0x01
			go g.tankMessage(id, tankReport{Cmd: "ir_window_result", Mask: &mask})
		}
	}
	stop := make(chan struct{})
	go g.ir.run(stop)
	t.Cleanup(func() { close(stop) })

	h := hostUp(t, g)
	var phones []*testPhone
	for i, name := range []string{"Alice", "Bob", "Carol"} {
		p := phone(t, g, name)
		p.send(inMsg{T: "join", Name: name})
		p.send(inMsg{T: "pick_tank", Tank: string(rune('a' + i))})
		phones = append(phones, p)
	}
	h.send(inMsg{T: "host_start"})
	g.mu.Lock()
	g.phaseEndsAt = time.Now()
	g.mu.Unlock()
	g.tick(200 * time.Millisecond)
	if g.phase != phasePlaying {
		t.Fatalf("phase %s, want playing", g.phase)
	}

	ft.mu.Lock()
	ft.offline = map[string]bool{"c": true} // Carol's tank drops off
	ft.mu.Unlock()

	start := time.Now()
	phones[0].send(inMsg{T: "fire"})
	text := phones[0].waitFor("fire_result")["text"].(string)
	if took := time.Since(start); took > time.Second {
		t.Fatalf("the shot took %v: it waited for the tank that is off", took)
	}
	if text != "Hit Bob on front" {
		t.Fatalf("Alice was told %q", text)
	}

	h.send(inMsg{T: "host_end"})
	g.events.close()
	evs := readEvents(t, evPath)
	starts, shots, ends := eventsOf(evs, "game_start"), eventsOf(evs, "shot"), eventsOf(evs, "game_end")
	if len(starts) != 1 || len(shots) != 1 || len(ends) != 1 {
		t.Fatalf("%d game_start, %d shot, %d game_end events; want one of each", len(starts), len(shots), len(ends))
	}
	if n := len(starts[0]["tanks"].([]any)); n != 3 {
		t.Errorf("game_start lists %d tanks, want 3", n)
	}
	s := shots[0]
	if s["result"] != "hit" || s["tank"] != "Desert-01" || s["player"] != "Alice" || s["listeners"] != 1.0 {
		t.Errorf("shot event: %v", s)
	}
	if off, _ := s["offline"].([]any); len(off) != 1 || off[0] != "Jungle-03" {
		t.Errorf("shot event's offline tanks: %v", s["offline"])
	}
	if hits, _ := s["hits"].([]any); len(hits) != 1 || hits[0].(map[string]any)["tank"] != "Jungle-02" {
		t.Errorf("shot event's hits: %v", s["hits"])
	}
	tally := ends[0]["shots"].(map[string]any)
	if tally["shots"] != 1.0 || tally["hits"] != 1.0 || ends[0]["code"] != "manual" {
		t.Errorf("game_end: %v", ends[0])
	}
}

// A shooter whose own tank has dropped off gets its "Miss" at once.
func TestShotFromATankThatIsOff(t *testing.T) {
	g, ft, w, _, a, _ := combatGame(t, func(s *Settings) { s.IrAckTimeoutMs = 3000 })
	w.set(map[string]int{"b": 1}, map[string]int{"b": 1})
	ft.mu.Lock()
	ft.offline = map[string]bool{"a": true}
	ft.mu.Unlock()
	time.Sleep(260 * time.Millisecond)
	start := time.Now()
	a.send(inMsg{T: "fire"})
	if text := a.waitFor("fire_result")["text"]; text != "Miss" {
		t.Fatalf("told %q", text)
	}
	if took := time.Since(start); took > time.Second {
		t.Fatalf("took %v: waited for an ack that can't come", took)
	}
	if hp := g.hpOf("b"); hp != 100 {
		t.Fatalf("Bob's HP %d", hp)
	}
}

func TestLogBundleHasEverything(t *testing.T) {
	dir := t.TempDir()
	serverPath, eventsPath := sessionLogPaths(dir)
	server := openRotFile(serverPath, 1000)
	_, _ = server.Write([]byte("hub line\n"))
	events := openEventLog(eventsPath)
	events.add("hub_start", nil)
	vlog := openVideoLog(filepath.Join(dir, "video.csv"))
	vlog.note("Desert-01", "boot", "started")
	t.Cleanup(func() { server.close(); events.close(); vlog.close() })

	b := &logBundle{server: server, events: events, video: vlog,
		extras: func() map[string]any { return map[string]any{"info.json": map[string]any{"version": "test"}} }}
	rec := httptest.NewRecorder()
	b.handle(rec, httptest.NewRequest(http.MethodGet, "/api/logs.zip", nil))
	zr, err := zip.NewReader(bytes.NewReader(rec.Body.Bytes()), int64(rec.Body.Len()))
	if err != nil {
		t.Fatal(err)
	}
	got := map[string]int{}
	for _, f := range zr.File {
		got[f.Name] = int(f.UncompressedSize64)
	}
	for _, name := range []string{"server.log", "events.jsonl", "video.csv", "info.json"} {
		if got[name] == 0 {
			t.Errorf("%s missing or empty in the bundle (has %v)", name, got)
		}
	}
}

// A camera that sends nothing at all is asked to start again (its first
// stream_on may have been lost, or its start failed), a limited number of
// times.
func TestCameraWithNoFramesIsAskedAgain(t *testing.T) {
	cam := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Error(w, "camera not started", http.StatusServiceUnavailable)
	}))
	defer cam.Close()
	old := videoURL
	videoURL = func(string) string { return cam.URL }
	defer func() { videoURL = old }()

	_, _, srv := newTestTankServer(t, "")
	c := dialTank(t, srv)
	sendJSON(t, c, map[string]any{"cmd": "hello", "id": "AABBCCDDEEFF", "name": "Desert-01", "ip": "127.0.0.1"})

	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	defer cancel()
	on := 0
	for on < 2 {
		_, msg, err := c.Read(ctx)
		if err != nil {
			t.Fatalf("stream_on arrived %d times, want it sent again: %v", on, err)
		}
		if strings.Contains(string(msg), `"stream_on"`) {
			on++
		}
	}
}

// A picture that stands still for a second or more gives one "video_freeze"
// event, written when the frames come back, saying whether the tank's
// heartbeats went quiet too.
func TestVideoFreezeEvent(t *testing.T) {
	path := filepath.Join(t.TempDir(), "events.jsonl")
	rl := newRelay()
	s := rl.get("a")
	s.setTankHB(tankVideoHB{at: time.Now().Add(-3 * time.Second), rssi: -40})
	ss := &statsSampler{rl: rl, events: openEventLog(path)}
	now := time.Now()
	ok := streamStats{ID: "a", Label: "Desert-01", Sources: 1, FPSIn: 25, MaxGapMs: 60}
	frozen := ok
	frozen.FPSIn, frozen.MaxGapMs = 0, 2000
	frozen2 := frozen
	frozen2.MaxGapMs = 4000
	for _, st := range []streamStats{ok, frozen, frozen2, ok, ok} {
		ss.freezes(now, []streamStats{st})
	}
	ss.events.close()
	got := eventsOf(readEvents(t, path), "video_freeze")
	if len(got) != 1 {
		t.Fatalf("%d freeze events, want 1: %v", len(got), got)
	}
	if got[0]["tank"] != "Desert-01" || got[0]["longest_wait_ms"] != 4000.0 || got[0]["hb_quiet_ms"].(float64) < 2900 {
		t.Fatalf("freeze event: %v", got[0])
	}
}
