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
			"vf": frames, "vb": frames * 12000, "vsend": frames * 30, "vmax": 180, "vmiss": 2, "temp": 41.5})
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
	if link == nil || link.RSSI != -71 || link.SendMsAvg != 30 || link.SendMsMax != 180 || link.CamMisses != 2 || link.ChipC == nil || *link.ChipC != 41.5 || link.FPSSent < 5 || link.FPSSent > 20 {
		t.Fatalf("tank link stats = %+v", link)
	}

	path := filepath.Join(t.TempDir(), "video.csv")
	vl := openVideoLog(path)
	defer vl.close()
	vl.rows(streamRows(time.Now(), streamStats{Label: "Desert-01", FPSIn: 19.5, KBPerFrame: 11.7, Tank: link, DrivePct: 62.4,
		Router:  &routerLinkStats{DBm: -20.5, RxMbitAvg: 58.5, RxMbitMin: 11, Rx11bPct: 25, TxMbitMin: 65},
		Viewers: []viewerStats{{Who: "Alice", FPS: 19.5}}})...)
	vl.phone("Desert-01", phoneVideoStats{Path: "img", ShownFps: 18, PageFps: 60, Name: "Alice", Device: "Pixel 7, Android 14"})
	b, _ := os.ReadFile(path)
	lines := strings.Split(strings.TrimSpace(string(b)), "\n")
	if len(lines) != 3 {
		t.Fatalf("want header + 2 lines, got:\n%s", b)
	}
	cols := len(strings.Split(lines[0], ","))
	if !strings.Contains(lines[1], ",tank,Desert-01,-71,19.5,11.7,") || !strings.Contains(lines[1], ",2,62,0,0,41.5,-20.5,58.5,11.0,25,65.0,Alice,19.5,") {
		t.Errorf("tank line: %s", lines[1])
	}
	if !strings.Contains(lines[2], ",phone,Desert-01,") || !strings.Contains(lines[2], `"Pixel 7, Android 14"`) {
		t.Errorf("phone line: %s", lines[2])
	}
	if n := len(strings.Split(lines[1], ",")); n != cols {
		t.Errorf("tank line has %d columns, header %d", n, cols)
	}
}

// The router's readings add up over a sample, then start afresh.
func TestRouterLinkWindow(t *testing.T) {
	l := &routerLink{win: map[string]*routerWindow{}}
	const mac = "90:70:69:18:F2:9C"
	l.add(parseAssocList(mac + "  -20 dBm / unknown\n\tRX: 72.0 MBit/s, MCS 7, 20MHz    0 Pkts.\n\tTX: 65.0 MBit/s, MCS 7, 20MHz    0 Pkts.\n"))
	l.add(parseAssocList(mac + "  -30 dBm / unknown\n\tRX: 11.0 MBit/s     0 Pkts.\n\tTX: 1.0 MBit/s     0 Pkts.\n"))
	l.add(parseAssocList(mac + "  -25 dBm / unknown\n\tRX: 6.0 MBit/s     0 Pkts.\n\tTX: 72.0 MBit/s, MCS 7, 20MHz    0 Pkts.\n"))
	l.add(parseAssocList(mac + "  -25 dBm / unknown\n\tRX: 13.0 MBit/s, MCS 1, 20MHz    0 Pkts.\n\tTX: 72.0 MBit/s, MCS 7, 20MHz    0 Pkts.\n"))
	got := l.take()[mac]
	if got == nil || got.DBm != -25 || got.RxMbitAvg != 25.5 || got.RxMbitMin != 6 || got.Rx11bPct != 25 || got.TxMbitMin != 1 {
		t.Fatalf("readings = %+v", got)
	}
	if again := l.take(); len(again) != 0 {
		t.Errorf("second take = %v", again)
	}
	var none *routerLink
	if none.take() != nil {
		t.Error("nil routerLink should have no readings")
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

// A server restart carries on the same log; a log with older columns is kept
// as .1 and a new one started.
func TestVideoLogSurvivesRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "video.csv")
	vl := openVideoLog(path)
	vl.note("Desert-01", "boot", "first run")
	vl.close()

	vl = openVideoLog(path)
	vl.note("Desert-01", "boot", "after restart")
	vl.close()
	b, _ := os.ReadFile(path)
	if s := string(b); strings.Count(s, videoLogHeader) != 1 || !strings.Contains(s, "first run") || !strings.Contains(s, "after restart") {
		t.Fatalf("log after restart:\n%s", s)
	}

	if err := os.WriteFile(path, []byte("time,kind,old,columns\nx,y,z,w\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	vl = openVideoLog(path)
	vl.close()
	old, _ := os.ReadFile(path + ".1")
	cur, _ := os.ReadFile(path)
	if !strings.Contains(string(old), "old,columns") || string(cur) != videoLogHeader {
		t.Fatalf("old %q, new %q", old, cur)
	}
}
