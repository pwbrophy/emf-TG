package main

import (
	"strings"
	"testing"
	"time"
)

// A hello's start-up report and a cam_status reach the tank list (and the
// host's tank page through it).
func TestBootReport(t *testing.T) {
	tm, _, srv := newTestTankServer(t, "")
	connected := make(chan string, 1)
	tm.onConnected = func(id string) { connected <- id }
	c := dialTank(t, srv)
	sendJSON(t, c, map[string]any{"cmd": "hello", "id": "AABBCCDDEEFF", "name": "Desert-01", "ip": "127.0.0.1",
		"up": 4200, "rst": "power-on", "wifi_ms": 1400, "wifi_tries": 1, "ch": 6, "rssi0": -31,
		"bssid": "94:83:C4:00:11:22", "cal": "full", "fw": "Oct 20 2026 10:12:00",
		"crash": "task httpd pc 0x4201abcd cause 28 bt 0x4201abcd 0x42005678"})
	<-connected
	sendJSON(t, c, map[string]any{"cmd": "cam_status", "ok": 1, "tries": 2, "err": 0})

	var boot string
	for deadline := time.Now().Add(2 * time.Second); time.Now().Before(deadline); time.Sleep(20 * time.Millisecond) {
		if l := tm.list(); len(l) == 1 && strings.Contains(l[0].Boot, "camera") {
			boot = l[0].Boot
			break
		}
	}
	for _, want := range []string{"started 4 s ago (power-on)", "Wi-Fi channel 6 in 1.4 s, -31 dBm",
		"firmware Oct 20 2026 10:12:00", "last crash: task httpd pc 0x4201abcd", "camera started on try 2"} {
		if !strings.Contains(boot, want) {
			t.Errorf("boot %q lacks %q", boot, want)
		}
	}
}

func TestCamStatusText(t *testing.T) {
	if got := camStatusText(false, 3, 0x105); got != "camera failed to start after 3 tries (error 0x105)" {
		t.Error(got)
	}
	if got := camStatusText(true, 1, 0); got != "camera started" {
		t.Error(got)
	}
}

// The host's Restart button sends the tank its reboot command.
func TestHostRestartsTank(t *testing.T) {
	g, ft := newTestGame(t, "Desert-01")
	h := hostUp(t, g)
	h.send(inMsg{T: "host_tank_restart", Tank: "a"})
	if ft.count(`a {"cmd":"reboot"}`) != 1 {
		t.Fatalf("reboot not sent: %v", ft.sent)
	}
}
