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
//
// The file lives in the temp folder (RAM on the router, so it is gone after a
// reboot) and is downloadable at GET /api/video-log. It is rotated at
// videoLogMaxBytes, keeping one older file.

import (
	"bufio"
	"log"
	"net/http"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"
)

const videoLogMaxBytes = 8 << 20

const videoLogHeader = "time,kind,tank,rssi_dbm,fps_in,kb_per_frame,tank_fps_sent,tank_send_ms_avg,tank_send_ms_max,tank_cam_misses," +
	"viewer,viewer_fps,viewer_dropped_per_s,phone_path,phone_shown_fps,phone_recv_fps,phone_decode_ms,phone_page_fps,phone_device\n"

type videoLog struct {
	mu   sync.Mutex
	path string
	f    *os.File
	w    *bufio.Writer
	size int64
}

// openVideoLog starts a fresh log at path. Returns nil (logging off) if the
// file can't be created; every method is safe on a nil *videoLog.
func openVideoLog(path string) *videoLog {
	if path == "" {
		return nil
	}
	vl := &videoLog{path: path}
	if err := vl.open(); err != nil {
		log.Printf("[VideoLog] can't write %s: %v", path, err)
		return nil
	}
	log.Printf("[VideoLog] writing %s", path)
	return vl
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
	base := []string{ts, "tank", s.Label, "", f1(s.FPSIn), f1(s.KBPerFrame), "", "", "", ""}
	if t := s.Tank; t != nil {
		base[3] = strconv.Itoa(t.RSSI)
		base[6] = f1(t.FPSSent)
		base[7] = f1(t.SendMsAvg)
		base[8] = strconv.Itoa(t.SendMsMax)
		base[9] = strconv.Itoa(t.CamMisses)
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
}

func (vl *videoLog) phone(tankLabel string, p phoneVideoStats) {
	if vl == nil {
		return
	}
	vl.rows([]string{time.Now().Format("15:04:05.0"), "phone", tankLabel, "", "", "", "", "", "", "",
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
}

// tankLinkStats is the latest heartbeat as shown in /api/stats and the log.
type tankLinkStats struct {
	RSSI      int     `json:"rssi"`      // Wi-Fi signal, dBm (closer to 0 is stronger)
	FPSSent   float64 `json:"fpsSent"`   // frames the tank sent per second
	SendMsAvg float64 `json:"sendMsAvg"` // average time to push one frame out
	SendMsMax int     `json:"sendMsMax"` // longest single frame push
	CamMisses int     `json:"camMisses"` // camera had no frame ready
}

func (h *tankVideoHB) stats(prevAt time.Time) *tankLinkStats {
	st := &tankLinkStats{RSSI: h.rssi, SendMsMax: h.sendMsMax, CamMisses: h.camMisses}
	if h.frames > 0 {
		st.SendMsAvg = float64(h.sendMsSum) / float64(h.frames)
	}
	if secs := h.at.Sub(prevAt).Seconds(); !prevAt.IsZero() && secs > 0.5 && secs < 10 {
		st.FPSSent = float64(h.frames) / secs
	}
	return st
}
