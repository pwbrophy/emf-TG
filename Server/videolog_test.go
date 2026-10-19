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
	vl.rows(streamRows(time.Now(), streamStats{Label: "Desert-01", FPSIn: 19.5, KBPerFrame: 11.7, Tank: link,
		Viewers: []viewerStats{{Who: "Alice", FPS: 19.5}}})...)
	vl.phone("Desert-01", phoneVideoStats{Path: "img", ShownFps: 18, PageFps: 60, Name: "Alice", Device: "Pixel 7, Android 14"})
	b, _ := os.ReadFile(path)
	lines := strings.Split(strings.TrimSpace(string(b)), "\n")
	if len(lines) != 3 {
		t.Fatalf("want header + 2 lines, got:\n%s", b)
	}
	cols := len(strings.Split(lines[0], ","))
	if !strings.Contains(lines[1], ",tank,Desert-01,-71,19.5,11.7,") || !strings.Contains(lines[1], ",Alice,19.5,") {
		t.Errorf("tank line: %s", lines[1])
	}
	if !strings.Contains(lines[2], ",phone,Desert-01,") || !strings.Contains(lines[2], `"Pixel 7, Android 14"`) {
		t.Errorf("phone line: %s", lines[2])
	}
	if n := len(strings.Split(lines[1], ",")); n != cols {
		t.Errorf("tank line has %d columns, header %d", n, cols)
	}
}
