package main

import (
	"strings"
	"testing"
	"time"
)

// sample builds one 2 s stats sample for a tank.
func sample(id string, fps float64, gapMs, sendMax, rssi int) streamStats {
	st := streamStats{ID: id, Label: id, Sources: 1, FPSIn: fps, MaxGapMs: gapMs}
	if rssi != 0 {
		st.Tank = &tankLinkStats{RSSI: rssi, SendMsMax: sendMax}
	}
	return st
}

// history fills 20 s of samples from make(i) for each tank, cameras on since t0.
func history(t0 time.Time, ids []string, make func(id string, i int) streamStats) (tankHistory, map[string]time.Time) {
	var h tankHistory
	since := map[string]time.Time{}
	for _, id := range ids {
		since[id] = t0
	}
	for i := 0; i < 10; i++ {
		var ss []streamStats
		for _, id := range ids {
			ss = append(ss, make(id, i))
		}
		h.add(t0.Add(cameraStartGrace+time.Duration(i)*2*time.Second), ss)
	}
	return h, since
}

var fleet = []string{"Desert-01", "Desert-04", "Jungle-07", "Jungle-08"}

// Desert-01 parked with a loose antenna (the 19 October 2026 log): sends of
// 80-350 ms against about 25 ms for the others, signal 12 dB weaker.
func TestCheckFlagsLooseAntenna(t *testing.T) {
	sends := []int{163, 106, 83, 115, 184, 355, 191, 107, 136, 70}
	h, since := history(time.Now(), fleet, func(id string, i int) streamStats {
		if id == "Desert-01" {
			return sample(id, 22, sends[i]+40, sends[i], -40)
		}
		return sample(id, 25, 60, 24, -28)
	})
	w := checkTanks(h, fleet, map[string]int{}, since)
	if len(w) != 1 || w[0].Tank != "Desert-01" {
		t.Fatalf("warnings = %+v", w)
	}
	all := strings.Join(w[0].Problems, "; ")
	if !strings.Contains(all, "its slowest frames take 125 ms to send (other tanks 24 ms)") || !strings.Contains(all, "12 dB weaker") {
		t.Errorf("problems: %s", all)
	}
	if w[0].Advice != adviceAntenna {
		t.Errorf("advice: %s", w[0].Advice)
	}
}

func TestCheckHealthyFleetIsQuiet(t *testing.T) {
	h, since := history(time.Now(), fleet, func(id string, i int) streamStats {
		return sample(id, 25, 50+i*3, 20+i, -30-i%3)
	})
	if w := checkTanks(h, fleet, map[string]int{"Desert-01": 25}, since); len(w) != 0 {
		t.Fatalf("warnings for a healthy fleet: %+v", w)
	}
}

func TestCheckFreezesLowFPSAndNoVideo(t *testing.T) {
	h, since := history(time.Now(), fleet, func(id string, i int) streamStats {
		switch {
		case id == "Desert-04" && i%3 == 0:
			return sample(id, 9, 1800, 1750, -30) // freezes
		case id == "Jungle-07":
			return sample(id, 8, 200, 30, 0) // old firmware, low frame rate
		case id == "Jungle-08" && i >= 7:
			return streamStats{ID: id, Label: id} // camera stopped
		}
		return sample(id, 25, 60, 24, -30)
	})
	w := checkTanks(h, fleet, map[string]int{"Jungle-07": 20}, since)
	got := map[string]string{}
	for _, x := range w {
		got[x.Tank] = strings.Join(x.Problems, "; ") + " | " + x.Advice
	}
	if !strings.Contains(got["Desert-04"], "picture froze 4 times") || !strings.Contains(got["Desert-04"], "up to 1.8 s") {
		t.Errorf("Desert-04: %q", got["Desert-04"])
	}
	if !strings.Contains(got["Jungle-07"], "video only 8 fps (should be 20)") {
		t.Errorf("Jungle-07: %q", got["Jungle-07"])
	}
	if got["Jungle-08"] != "no video from its camera | "+adviceRestart {
		t.Errorf("Jungle-08: %q", got["Jungle-08"])
	}
	if _, ok := got["Desert-01"]; ok {
		t.Errorf("Desert-01 flagged: %q", got["Desert-01"])
	}
}

// Figures from before a camera came on (e.g. off during the last game) don't
// count, and a tank with too few figures since isn't judged.
func TestCheckIgnoresFiguresBeforeCameraOn(t *testing.T) {
	t0 := time.Now()
	h, since := history(t0, fleet, func(id string, i int) streamStats {
		if id == "Desert-01" && i < 6 {
			return streamStats{ID: id, Label: id} // camera was off
		}
		return sample(id, 25, 60, 24, -30)
	})
	since["Desert-01"] = t0.Add(12 * time.Second) // switched on at sample 6
	if w := checkTanks(h, fleet, map[string]int{}, since); len(w) != 0 {
		t.Fatalf("warnings: %+v", w)
	}
	delete(since, "Desert-01") // camera off: not judged at all
	if w := checkTanks(h, fleet, map[string]int{}, since); len(w) != 0 {
		t.Fatalf("warnings with camera off: %+v", w)
	}
}
