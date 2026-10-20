package main

// Tank start-up reports. Since October 2026 each tank's hello says how its
// last start went: why it started (power-on, software = OTA or the Restart
// button, brownout, crash...), how long ago, how Wi-Fi connected and which
// firmware build it runs. The server logs this so a tank that misbehaves can
// be compared with its good starts (Desert-01's slow video on 19 October
// 2026 went away with a power cycle, and nothing showed what had differed).

import (
	"fmt"
	"strings"
	"time"
)

// bootReport is the start-up part of a hello. Older firmware leaves it out.
type bootReport struct {
	UpMs      int64  `json:"up"`         // ms since the tank started
	Reset     string `json:"rst"`        // why it started
	WifiMs    int64  `json:"wifi_ms"`    // ms from start to Wi-Fi connected
	WifiTries int    `json:"wifi_tries"` // network attempts it took
	Channel   int    `json:"ch"`
	RSSI      int    `json:"rssi0"` // signal (dBm) when it connected
	BSSID     string `json:"bssid"` // the access point it joined
	Cal       string `json:"cal"`   // radio calibration: "full"
	Firmware  string `json:"fw"`    // build date and time
	// The last crash's summary from the tank's core dump (task, pc, cause,
	// backtrace), reported once; "" = none. Decode the addresses with
	// xtensa-esp32s3-elf-addr2line -pfiaC -e firmware.elf <addresses> using
	// the build that crashed.
	Crash string `json:"crash"`
}

func (b bootReport) known() bool { return b.Reset != "" }

// describe says how the tank started, as of a hello received at helloAt.
func (b bootReport) describe(helloAt, now time.Time) string {
	if !b.known() {
		return ""
	}
	started := helloAt.Add(-time.Duration(b.UpMs) * time.Millisecond)
	tries := ""
	if b.WifiTries > 1 {
		tries = fmt.Sprintf(", %d tries", b.WifiTries)
	}
	text := fmt.Sprintf("started %s ago (%s); Wi-Fi channel %d in %.1f s%s, %d dBm; firmware %s",
		ago(now.Sub(started)), b.Reset, b.Channel, float64(b.WifiMs)/1000, tries, b.RSSI, b.Firmware)
	if b.Crash != "" {
		text += "; last crash: " + b.Crash
	}
	return text
}

// ago is a short, rounded duration: "8 s", "4 min", "2 h 5 min".
func ago(d time.Duration) string {
	switch {
	case d < time.Minute:
		return fmt.Sprintf("%d s", int(d.Seconds()))
	case d < time.Hour:
		return fmt.Sprintf("%d min", int(d.Minutes()))
	default:
		return fmt.Sprintf("%d h %d min", int(d.Hours()), int(d.Minutes())%60)
	}
}

// camStatusText describes a camera start reported by cam_status.
func camStatusText(ok bool, tries, err int) string {
	switch {
	case !ok:
		return fmt.Sprintf("camera failed to start after %d tries (error 0x%x)", tries, err)
	case tries > 1:
		return fmt.Sprintf("camera started on try %d", tries)
	default:
		return "camera started"
	}
}

// note writes a "boot" or "camera" line to the video log.
func (vl *videoLog) note(tank, kind, text string) {
	if vl == nil {
		return
	}
	row := make([]string, strings.Count(videoLogHeader, ","))
	row[0], row[1], row[2] = time.Now().Format("15:04:05.0"), kind, tank
	row = append(row, text)
	vl.rows(row)
}
