package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// A tank's heartbeat reaches the relay's stats, and a stats sample and a
// phone report end up in the video log.
func TestVideoLogFromHeartbeat(t *testing.T) {
	tm, rl, srv := newTestTankServer(t, "")
	connected := make(chan string, 1)
	tm.onConnected = func(id string) { connected <- id }
	c := dialTank(t, srv)
	sendJSON(t, c, map[string]any{"cmd": "hello", "id": "AABBCCDDEEFF", "name": "Desert-01", "ip": "127.0.0.1"})
	<-connected
	s := rl.get("AABBCCDDEEFF")

	hb := func(frames int) {
		sendJSON(t, c, map[string]any{"cmd": "hb", "t": 1, "heap": 100000, "rssi": -71,
			"vf": frames, "vb": frames * 12000, "vsend": frames * 30, "vmax": 180, "vmiss": 2})
	}
	hb(40)
	time.Sleep(600 * time.Millisecond)
	hb(10)
	var link *tankLinkStats
	for deadline := time.Now().Add(2 * time.Second); time.Now().Before(deadline); time.Sleep(20 * time.Millisecond) {
		if link = s.tankLink(); link != nil && link.FPSSent > 0 {
			break
		}
	}
	if link == nil || link.RSSI != -71 || link.SendMsAvg != 30 || link.SendMsMax != 180 || link.CamMisses != 2 || link.FPSSent < 5 || link.FPSSent > 20 {
		t.Fatalf("tank link stats = %+v", link)
	}

	path := filepath.Join(t.TempDir(), "video.csv")
	vl := openVideoLog(path)
	defer vl.close()
	vl.rows(streamRows(time.Now(), streamStats{Label: "Desert-01", FPSIn: 19.5, KBPerFrame: 11.7, Tank: link, DrivePct: 62.4,
		Viewers: []viewerStats{{Who: "Alice", FPS: 19.5}}})...)
	vl.phone("Desert-01", phoneVideoStats{Path: "img", ShownFps: 18, PageFps: 60, Name: "Alice", Device: "Pixel 7, Android 14"})
	b, _ := os.ReadFile(path)
	lines := strings.Split(strings.TrimSpace(string(b)), "\n")
	if len(lines) != 3 {
		t.Fatalf("want header + 2 lines, got:\n%s", b)
	}
	cols := len(strings.Split(lines[0], ","))
	if !strings.Contains(lines[1], ",tank,Desert-01,-71,19.5,11.7,") || !strings.Contains(lines[1], ",2,62,0,0,Alice,19.5,") {
		t.Errorf("tank line: %s", lines[1])
	}
	if !strings.Contains(lines[2], ",phone,Desert-01,") || !strings.Contains(lines[2], `"Pixel 7, Android 14"`) {
		t.Errorf("phone line: %s", lines[2])
	}
	if n := len(strings.Split(lines[1], ",")); n != cols {
		t.Errorf("tank line has %d columns, header %d", n, cols)
	}
}

// Drive time stops at the tank's own watchdog; the turret runs until told.
func TestMotorTracking(t *testing.T) {
	s := newRelay().get("1")
	t0 := time.Now()
	s.noteCommand(`{"cmd":"set_hp","hp":50,"max":100}`, t0) // ignored
	for i := 0; i < 10; i++ {                               // 0.5 s of driving at 20 Hz
		s.noteCommand(`{"cmd":"drive","l":1.000,"r":-1.000}`, t0.Add(time.Duration(i)*50*time.Millisecond))
	}
	s.noteCommand(`{"cmd":"turret","speed":0.500}`, t0.Add(time.Second))
	s.noteCommand(`{"cmd":"turret","speed":0.000}`, t0.Add(1500*time.Millisecond))
	s.noteCommand(`{"cmd":"turret","speed":-0.800}`, t0.Add(1800*time.Millisecond))

	drive, turret := s.takeMotors(t0.Add(2 * time.Second))
	// Last drive command at 450 ms + 500 ms watchdog = 950 ms of driving.
	if drive != 950*time.Millisecond {
		t.Errorf("drive = %v, want 950ms", drive)
	}
	// 1.0-1.5 s and 1.8-2.0 s.
	if turret != 700*time.Millisecond {
		t.Errorf("turret = %v, want 700ms", turret)
	}
	// The turret is still turning in the next window; the tracks are not.
	drive, turret = s.takeMotors(t0.Add(3 * time.Second))
	if drive != 0 || turret != time.Second {
		t.Errorf("second window: drive %v turret %v, want 0 and 1s", drive, turret)
	}
	// Stick at zero still sends drive commands: not driving.
	s.noteCommand(`{"cmd":"drive","l":0.000,"r":0.020}`, t0.Add(3100*time.Millisecond))
	if drive, _ = s.takeMotors(t0.Add(4 * time.Second)); drive != 0 {
		t.Errorf("drive with stick centred = %v", drive)
	}
}
