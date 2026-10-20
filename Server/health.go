package main

// Tank check on Start: warns the host about a tank whose video or Wi-Fi looks
// wrong before a game begins, so it can be restarted or have its antenna
// tightened first.
//
// Every camera streams in the lobby (cameraWanted), so the stats sampler has
// the last healthWindow of 2 s samples for each tank. A tank is flagged when:
//   - no frames have arrived for the last few samples;
//   - its frame rate is well under its camera's cap;
//   - its picture froze (no frame for stallMs or more) more than once;
//   - its frames take far longer to send than the other tanks' do (the
//     loose-antenna signature: Desert-01, October 2026, was 120-350 ms when
//     parked against about 25 ms for the rest);
//   - its Wi-Fi signal is weak, or much weaker than the other tanks' in the
//     same room.
// The host can still start anyway.

import (
	"fmt"
	"sort"
	"time"
)

const (
	healthWindow     = 20 * time.Second
	cameraStartGrace = 3 * time.Second // a camera's first frames take a moment
	healthMinSamples = 4               // 8 s of figures before a tank is judged
	noVideoSamples   = 3               // this many frameless samples in a row = no video
	lowFPSShare      = 0.6             // below this share of the camera's cap = low frame rate
	stallMs          = 500             // a wait this long between frames is a freeze
	stallsToWarn     = 2
	slowerFactor     = 3.0 // this many times slower than the other tanks...
	slowFloorMs      = 100 // ...and at least this slow
	weakSignalDBm    = -75
	signalGapDB      = 10 // this much weaker than the other tanks
	defaultFPSCap    = 20
)

const (
	adviceRestart = "Give it a restart."
	adviceAntenna = "Give it a restart or check its antenna is tight."
)

// tankWarning is one tank's problems, shown to the host on Start.
type tankWarning struct {
	ID       string   `json:"id"`   // the tank's id (for the Restart offer)
	Tank     string   `json:"tank"` // the tank's name
	Problems []string `json:"problems"`
	Advice   string   `json:"advice"`
}

type healthSample struct {
	at time.Time
	st streamStats
}

// tankHistory keeps each tank's recent stats samples.
type tankHistory map[string][]healthSample

func (h *tankHistory) add(at time.Time, streams []streamStats) {
	if *h == nil {
		*h = tankHistory{}
	}
	for _, st := range streams {
		(*h)[st.ID] = append((*h)[st.ID], healthSample{at, st})
	}
	for id, ss := range *h {
		i := 0
		for i < len(ss) && at.Sub(ss[i].at) > healthWindow {
			i++
		}
		if i == len(ss) {
			delete(*h, id)
		} else {
			(*h)[id] = ss[i:]
		}
	}
}

// tankFigures sums up one tank's recent samples.
type tankFigures struct {
	label     string
	samples   int
	noVideo   bool
	fps       float64 // median frames per second into the hub
	stalls    int     // samples with a freeze
	worstMs   int     // longest freeze or frame send
	sendMaxMs float64 // median of the tank's own longest send per sample; -1 = old firmware
	gapMs     float64 // median longest wait between frames at the hub
	rssi      float64 // median signal; 0 = not reported
}

func figuresFor(ss []healthSample) tankFigures {
	f := tankFigures{samples: len(ss), sendMaxMs: -1}
	if len(ss) == 0 {
		return f
	}
	f.label = ss[len(ss)-1].st.Label
	var fps, gaps, sends, rssi []float64
	for _, s := range ss {
		st := s.st
		fps = append(fps, st.FPSIn)
		gaps = append(gaps, float64(st.MaxGapMs))
		worst := st.MaxGapMs
		if t := st.Tank; t != nil {
			sends = append(sends, float64(t.SendMsMax))
			worst = max(worst, t.SendMsMax)
			if t.RSSI != 0 {
				rssi = append(rssi, float64(t.RSSI))
			}
		}
		if worst >= stallMs {
			f.stalls++
		}
		f.worstMs = max(f.worstMs, worst)
	}
	f.fps, f.gapMs, f.rssi = median(fps), median(gaps), median(rssi)
	if len(sends) >= len(ss)/2 {
		f.sendMaxMs = median(sends)
	}
	if len(ss) >= noVideoSamples {
		f.noVideo = true
		for _, s := range ss[len(ss)-noVideoSamples:] {
			if s.st.FPSIn > 0 {
				f.noVideo = false
			}
		}
	}
	return f
}

// checkTanks judges the tanks in ids (about to play) against every tank with
// recent figures. fpsCap is each tank's camera frame-rate cap (0 = unknown);
// camSince is when each tank's camera was last switched on (only samples
// after that count; a tank without an entry isn't streaming and isn't judged).
func checkTanks(h tankHistory, ids []string, fpsCap map[string]int, camSince map[string]time.Time) []tankWarning {
	figs := map[string]tankFigures{}
	for id, ss := range h {
		since, on := camSince[id]
		if !on {
			continue
		}
		i := 0
		for i < len(ss) && ss[i].at.Before(since.Add(cameraStartGrace)) {
			i++
		}
		figs[id] = figuresFor(ss[i:])
	}
	var out []tankWarning
	for _, id := range ids {
		f, ok := figs[id]
		if !ok || f.samples < healthMinSamples {
			continue // not enough figures yet (camera only just switched on)
		}
		var others []tankFigures
		for oid, of := range figs {
			if oid != id && of.samples >= healthMinSamples && !of.noVideo {
				others = append(others, of)
			}
		}
		if p := problemsFor(f, others, fpsCap[id]); len(p) > 0 {
			advice := adviceAntenna
			if f.noVideo {
				advice = adviceRestart
			}
			out = append(out, tankWarning{ID: id, Tank: f.label, Problems: p, Advice: advice})
		}
	}
	sort.Slice(out, func(i, j int) bool { return naturalLess(out[i].Tank, out[j].Tank) })
	return out
}

func problemsFor(f tankFigures, others []tankFigures, fpsCap int) []string {
	if f.noVideo {
		return []string{"no video from its camera"}
	}
	if fpsCap <= 0 {
		fpsCap = defaultFPSCap
	}
	var p []string
	if f.fps < lowFPSShare*float64(fpsCap) {
		p = append(p, fmt.Sprintf("video only %.0f fps (should be %d)", f.fps, fpsCap))
	}
	if f.stalls >= stallsToWarn {
		p = append(p, fmt.Sprintf("picture froze %d times in the last %.0f s (up to %s)",
			f.stalls, healthWindow.Seconds(), msText(f.worstMs)))
	}

	// Slow to send compared with the other tanks: the tank's own figure when
	// it and at least two others report it, otherwise the waits at the hub.
	mine, theirs, what := f.gapMs, []float64{}, "its longest gaps between frames are %s (other tanks %s)"
	var sendOthers []float64
	for _, o := range others {
		if o.sendMaxMs >= 0 {
			sendOthers = append(sendOthers, o.sendMaxMs)
		}
		theirs = append(theirs, o.gapMs)
	}
	if f.sendMaxMs >= 0 && len(sendOthers) >= 2 {
		mine, theirs, what = f.sendMaxMs, sendOthers, "its slowest frames take %s to send (other tanks %s)"
	}
	if len(theirs) >= 2 {
		typical := median(theirs)
		if mine >= slowFloorMs && mine > slowerFactor*typical {
			p = append(p, fmt.Sprintf(what, msText(int(mine)), msText(int(typical))))
		}
	}

	if f.rssi != 0 {
		var rs []float64
		for _, o := range others {
			if o.rssi != 0 {
				rs = append(rs, o.rssi)
			}
		}
		switch {
		case f.rssi < weakSignalDBm:
			p = append(p, fmt.Sprintf("weak Wi-Fi signal (%.0f dBm)", f.rssi))
		case len(rs) >= 2 && median(rs)-f.rssi >= signalGapDB:
			p = append(p, fmt.Sprintf("Wi-Fi signal %.0f dB weaker than the other tanks", median(rs)-f.rssi))
		}
	}
	return p
}

// check runs checkTanks on the sampler's history.
func (ss *statsSampler) check(ids []string, fpsCap map[string]int, camSince map[string]time.Time) []tankWarning {
	ss.mu.Lock()
	defer ss.mu.Unlock()
	return checkTanks(ss.health, ids, fpsCap, camSince)
}

func msText(ms int) string {
	if ms >= 1000 {
		return fmt.Sprintf("%.1f s", float64(ms)/1000)
	}
	return fmt.Sprintf("%d ms", ms)
}

func median(v []float64) float64 {
	if len(v) == 0 {
		return 0
	}
	s := append([]float64(nil), v...)
	sort.Float64s(s)
	if len(s)%2 == 1 {
		return s[len(s)/2]
	}
	return (s[len(s)/2-1] + s[len(s)/2]) / 2
}
