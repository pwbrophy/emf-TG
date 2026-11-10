package main

// Video log: a CSV file for working out why a feed was choppy or laggy.
//
// Every stats interval (2 s), one line per tank that is sending video or being
// watched: the tank's Wi-Fi signal and how long it took to push its frames out
// (from its heartbeat), the frame rate that reached the server, and what each
// viewer got. Each phone also reports, every 30 s, what it actually drew.
// Reading a choppy moment across one tank's lines:
//   - signal weak (below about -75 dBm) and fps in low: the tank's antenna or
//     position.
//   - signal fine, fps in low and tank send ms high: the air is busy (other
//     tanks, other networks), not this tank's antenna.
//   - fps in fine, viewer fps low or dropped high: the link to that phone.
//   - viewer fps fine, phone shown fps low: the phone itself.
// drive_pct and turret_pct say how much of each sample the tank's tracks and
// turret were running, so a feed that only breaks up while the tank moves
// points at motor noise rather than the radio link itself. chip_temp_c is the
// tank chip's own temperature: tanks send worse for a few minutes after a
// cold start, and this shows whether that follows the chip warming up.
// The router_ columns are the router's own view of the tank (routerlink.go):
// a low router_rx_mbit_min means the tank is taking airtime from the others,
// and router_rx_11b_pct says how much of that was at an 802.11b rate.
//
// The file lives in the temp folder (RAM on the router, so it is gone after a
// reboot) and is downloadable at GET /api/video-log. It is rotated at
// videoLogMaxBytes, keeping one older file. A server restart (a deploy)
// carries on the same file; it used to start afresh and lost the game just
// played.

import (
	"bufio"
	"encoding/json"
	"log"
	"math"
	"net/http"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"
)

const videoLogMaxBytes = 8 << 20

const videoLogHeader = "time,kind,tank,rssi_dbm,fps_in,kb_per_frame,tank_fps_sent,tank_send_ms_avg,tank_send_ms_max,tank_cam_misses,drive_pct,turret_pct,max_gap_ms,chip_temp_c," +
	"router_dbm,router_rx_mbit_avg,router_rx_mbit_min,router_rx_11b_pct,router_tx_mbit_min," +
	"viewer,viewer_fps,viewer_dropped_per_s,phone_path,phone_shown_fps,phone_recv_fps,phone_decode_ms,phone_page_fps,device_or_note\n"

type videoLog struct {
	mu   sync.Mutex
	path string
	f    *os.File
	w    *bufio.Writer
	size int64
}

// openVideoLog carries on the log at path, or starts one if there is none
// (or it has older columns: that one is kept as path.1). Returns nil (logging
// off) if the file can't be written; every method is safe on a nil *videoLog.
func openVideoLog(path string) *videoLog {
	if path == "" {
		return nil
	}
	vl := &videoLog{path: path}
	if err := vl.reopen(); err != nil {
		log.Printf("[VideoLog] can't write %s: %v", path, err)
		return nil
	}
	log.Printf("[VideoLog] writing %s", path)
	return vl
}

// reopen appends to the existing file if it has the current columns.
func (vl *videoLog) reopen() error {
	if b, err := os.ReadFile(vl.path); err == nil && len(b) > 0 {
		if strings.HasPrefix(string(b), videoLogHeader) {
			f, err := os.OpenFile(vl.path, os.O_APPEND|os.O_WRONLY, 0o644)
			if err != nil {
				return err
			}
			vl.f, vl.w, vl.size = f, bufio.NewWriter(f), int64(len(b))
			return nil
		}
		_ = os.Rename(vl.path, vl.path+".1")
	}
	return vl.open()
}

func (vl *videoLog) open() error {
	f, err := os.Create(vl.path)
	if err != nil {
		return err
	}
	vl.f, vl.w, vl.size = f, bufio.NewWriter(f), 0
	vl.write(videoLogHeader)
	return nil
}

func (vl *videoLog) write(s string) {
	n, _ := vl.w.WriteString(s)
	vl.size += int64(n)
}

// rows appends lines and flushes them to the file. Fields are CSV-escaped.
func (vl *videoLog) rows(rows ...[]string) {
	if vl == nil || len(rows) == 0 {
		return
	}
	vl.mu.Lock()
	defer vl.mu.Unlock()
	if vl.f == nil {
		return
	}
	for _, r := range rows {
		for i, field := range r {
			if i > 0 {
				vl.write(",")
			}
			vl.write(csvField(field))
		}
		vl.write("\n")
	}
	_ = vl.w.Flush()
	if vl.size >= videoLogMaxBytes {
		_ = vl.f.Close()
		_ = os.Rename(vl.path, vl.path+".1")
		if err := vl.open(); err != nil {
			log.Printf("[VideoLog] rotate failed, logging off: %v", err)
			vl.f = nil
		}
	}
}

// close flushes and closes the file (shutdown, tests).
func (vl *videoLog) close() {
	if vl == nil {
		return
	}
	vl.mu.Lock()
	defer vl.mu.Unlock()
	if vl.f != nil {
		_ = vl.w.Flush()
		_ = vl.f.Close()
		vl.f = nil
	}
}

// flush writes anything still waiting to the file (before it is read).
func (vl *videoLog) flush() {
	if vl == nil {
		return
	}
	vl.mu.Lock()
	if vl.f != nil {
		_ = vl.w.Flush()
	}
	vl.mu.Unlock()
}

// handle serves the log: GET /api/video-log (?old=1 for the rotated file).
func (vl *videoLog) handle(w http.ResponseWriter, r *http.Request) {
	if vl == nil {
		http.Error(w, "video log is off", http.StatusNotFound)
		return
	}
	path := vl.path
	if r.URL.Query().Get("old") != "" {
		path += ".1"
	}
	vl.mu.Lock()
	if vl.w != nil {
		_ = vl.w.Flush()
	}
	vl.mu.Unlock()
	w.Header().Set("Content-Type", "text/csv; charset=utf-8")
	w.Header().Set("Content-Disposition", `attachment; filename="video-log.csv"`)
	w.Header().Set("Cache-Control", "no-store")
	http.ServeFile(w, r, path)
}

// streamRows turns one stats sample into log lines: one per viewer, or one
// with the viewer columns empty when nobody is watching.
func streamRows(now time.Time, s streamStats) [][]string {
	ts := now.Format("15:04:05.0")
	base := []string{ts, "tank", s.Label, "", f1(s.FPSIn), f1(s.KBPerFrame), "", "", "", "",
		strconv.Itoa(int(s.DrivePct + 0.5)), strconv.Itoa(int(s.TurretPct + 0.5)), strconv.Itoa(s.MaxGapMs), "", "", "", "", "", ""}
	if t := s.Tank; t != nil {
		base[3] = strconv.Itoa(t.RSSI)
		base[6] = f1(t.FPSSent)
		base[7] = f1(t.SendMsAvg)
		base[8] = strconv.Itoa(t.SendMsMax)
		base[9] = strconv.Itoa(t.CamMisses)
		if t.ChipC != nil {
			base[13] = f1(*t.ChipC)
		}
	}
	if r := s.Router; r != nil {
		base[14] = f1(r.DBm)
		base[15] = f1(r.RxMbitAvg)
		base[16] = f1(r.RxMbitMin)
		base[17] = strconv.Itoa(int(r.Rx11bPct + 0.5))
		base[18] = f1(r.TxMbitMin)
	}
	if len(s.Viewers) == 0 {
		return [][]string{append(base, "", "", "", "", "", "", "", "", "")}
	}
	out := make([][]string, 0, len(s.Viewers))
	for _, v := range s.Viewers {
		row := append(append([]string{}, base...), v.Who, f1(v.FPS), f1(v.Dropped), "", "", "", "", "", "")
		out = append(out, row)
	}
	return out
}

// phoneVideoStats is what play.html measures on the phone (vsMeasure).
type phoneVideoStats struct {
	Path     string  `json:"path"`     // "ws" (canvas) or "img"
	ShownFps float64 `json:"shownFps"` // frames actually put on screen
	RecvFps  float64 `json:"recvFps"`  // frames received (ws path only)
	DecodeMs float64 `json:"decodeMs"` // average JPEG decode time (ws path only)
	PageFps  float64 `json:"pageFps"`  // animation frame rate: low = phone struggling
	Name     string  `json:"name"`
	Device   string  `json:"device"`

	// For the event log only (the video log's columns stay as they are).
	RttMs    float64 `json:"rttMs"`    // average round trip to the hub since the last report
	RttMaxMs float64 `json:"rttMaxMs"` // the longest
	RttN     int     `json:"rttN"`     // how many were timed (0 = an older page)
	Cores    int     `json:"cores"`
	MemoryGb float64 `json:"memoryGb"`
	Screen   string  `json:"screen"`
	LowFx    bool    `json:"lowFx"`
}

func (vl *videoLog) phone(tankLabel string, p phoneVideoStats) {
	if vl == nil {
		return
	}
	vl.rows([]string{time.Now().Format("15:04:05.0"), "phone", tankLabel, "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
		viewerName(p.Name), "", "", p.Path, f1(p.ShownFps), f1(p.RecvFps), f1(p.DecodeMs), f1(p.PageFps), p.Device})
}

func f1(v float64) string { return strconv.FormatFloat(v, 'f', 1, 64) }

func csvField(s string) string {
	if !strings.ContainsAny(s, ",\"\r\n") {
		return s
	}
	return `"` + strings.ReplaceAll(s, `"`, `""`) + `"`
}

// tankVideoHB is the video part of a tank's heartbeat (sent every 2 s).
type tankVideoHB struct {
	at        time.Time
	rssi      int
	frames    int // frames sent since the previous heartbeat
	sendMsSum int
	sendMsMax int
	camMisses int
	chipC     *float64 // the chip's temperature, °C; nil = not reported
}

// tankLinkStats is the latest heartbeat as shown in /api/stats and the log.
type tankLinkStats struct {
	RSSI      int      `json:"rssi"`            // Wi-Fi signal, dBm (closer to 0 is stronger)
	FPSSent   float64  `json:"fpsSent"`         // frames the tank sent per second
	SendMsAvg float64  `json:"sendMsAvg"`       // average time to push one frame out
	SendMsMax int      `json:"sendMsMax"`       // longest single frame push
	CamMisses int      `json:"camMisses"`       // camera had no frame ready
	ChipC     *float64 `json:"chipC,omitempty"` // the tank chip's temperature, °C
}

func (h *tankVideoHB) stats(prevAt time.Time) *tankLinkStats {
	st := &tankLinkStats{RSSI: h.rssi, SendMsMax: h.sendMsMax, CamMisses: h.camMisses, ChipC: h.chipC}
	if h.frames > 0 {
		st.SendMsAvg = float64(h.sendMsSum) / float64(h.frames)
	}
	if secs := h.at.Sub(prevAt).Seconds(); !prevAt.IsZero() && secs > 0.5 && secs < 10 {
		st.FPSSent = float64(h.frames) / secs
	}
	return st
}

// ── Motor activity ──────────────────────────────────────────────────────────

// Below this a drive or turret value doesn't move the motors.
const motorDeadZone = 0.05

// A tank stops its tracks itself when no drive command arrives for this long
// (DRIVE_WATCHDOG_MS in the firmware); the turret keeps going until told.
const tankDriveWatchdog = 500 * time.Millisecond

// motorChannel adds up how long one motor (tracks or turret) was running.
type motorChannel struct {
	on      bool
	since   time.Time // when it started running, or the current window's start
	lastCmd time.Time
	accum   time.Duration
	timeout time.Duration // 0 = runs until told to stop
}

func (m *motorChannel) set(on bool, now time.Time) {
	m.settle(now)
	if on && !m.on {
		m.since = now
	} else if !on && m.on {
		m.accum += now.Sub(m.since)
	}
	m.on = on
	m.lastCmd = now
}

// settle stops a motor the tank's own watchdog will have stopped.
func (m *motorChannel) settle(now time.Time) {
	if m.on && m.timeout > 0 && now.Sub(m.lastCmd) > m.timeout {
		m.accum += m.lastCmd.Add(m.timeout).Sub(m.since)
		m.on = false
	}
}

// take returns the running time since the last take.
func (m *motorChannel) take(now time.Time) time.Duration {
	m.settle(now)
	d := m.accum
	if m.on {
		d += now.Sub(m.since)
		m.since = now
	}
	m.accum = 0
	return d
}

type motorTracker struct {
	drive, turret motorChannel
}

func newMotorTracker() motorTracker {
	return motorTracker{drive: motorChannel{timeout: tankDriveWatchdog}}
}

// motorCommand reads a drive or turret command on its way to a tank.
// ok is false for every other command.
func motorCommand(msg string) (isDrive, running, ok bool) {
	switch {
	case strings.HasPrefix(msg, `{"cmd":"drive"`):
		var d struct{ L, R float64 }
		if json.Unmarshal([]byte(msg), &d) != nil {
			return false, false, false
		}
		return true, math.Abs(d.L) > motorDeadZone || math.Abs(d.R) > motorDeadZone, true
	case strings.HasPrefix(msg, `{"cmd":"turret"`):
		var t struct{ Speed float64 }
		if json.Unmarshal([]byte(msg), &t) != nil {
			return false, false, false
		}
		return false, math.Abs(t.Speed) > motorDeadZone, true
	}
	return false, false, false
}
