package main

// CPU and memory figures read from /proc, so the stats show how hard the
// router is working: overall busy %, per core, time spent handling network
// interrupts (softirq), and how much of it is this server.

import (
	"os"
	"strconv"
	"strings"
	"time"
)

type systemStats struct {
	Cores         int       `json:"cores"`
	CPUBusyPct    float64   `json:"cpuBusyPct"`    // whole machine, all cores averaged
	CPUCoresPct   []float64 `json:"cpuCoresPct"`   // per core
	CPUSoftirqPct float64   `json:"cpuSoftirqPct"` // network/interrupt work, all cores averaged
	ProcCPUPct    float64   `json:"procCpuPct"`    // this server, % of the whole machine
	RSSMB         float64   `json:"rssMB"`
	MemAvailMB    float64   `json:"memAvailMB"`
	Load1         float64   `json:"load1"`
}

type cpuTimes struct{ total, idle, softirq uint64 }

type systemSampler struct {
	lastAt    time.Time
	lastAll   cpuTimes
	lastCores []cpuTimes
	lastProc  uint64
}

// Linux reports process CPU time in clock ticks of 1/100 s on every platform
// we target (USER_HZ is 100 on arm64 and amd64).
const clockTicksPerSec = 100

func newSystemSampler() *systemSampler {
	s := &systemSampler{lastAt: time.Now()}
	s.lastAll, s.lastCores = readCPUTimes()
	s.lastProc = readProcTicks()
	return s
}

func (s *systemSampler) sample() *systemStats {
	now := time.Now()
	all, cores := readCPUTimes()
	proc := readProcTicks()
	elapsed := now.Sub(s.lastAt).Seconds()

	st := &systemStats{Cores: len(cores)}
	st.CPUBusyPct, st.CPUSoftirqPct = busyAndSoftirq(s.lastAll, all)
	for i := range cores {
		if i < len(s.lastCores) {
			busy, _ := busyAndSoftirq(s.lastCores[i], cores[i])
			st.CPUCoresPct = append(st.CPUCoresPct, round1(busy))
		}
	}
	if elapsed > 0 && len(cores) > 0 {
		st.ProcCPUPct = round1(float64(proc-s.lastProc) / clockTicksPerSec / elapsed / float64(len(cores)) * 100)
	}
	st.CPUBusyPct = round1(st.CPUBusyPct)
	st.CPUSoftirqPct = round1(st.CPUSoftirqPct)
	st.RSSMB = round1(readKBField("/proc/self/status", "VmRSS:") / 1024)
	st.MemAvailMB = round1(readKBField("/proc/meminfo", "MemAvailable:") / 1024)
	if b, err := os.ReadFile("/proc/loadavg"); err == nil {
		if f := strings.Fields(string(b)); len(f) > 0 {
			st.Load1, _ = strconv.ParseFloat(f[0], 64)
		}
	}

	s.lastAt, s.lastAll, s.lastCores, s.lastProc = now, all, cores, proc
	return st
}

func busyAndSoftirq(a, b cpuTimes) (busy, softirq float64) {
	total := float64(b.total - a.total)
	if total <= 0 {
		return 0, 0
	}
	return (total - float64(b.idle-a.idle)) / total * 100, float64(b.softirq-a.softirq) / total * 100
}

// readCPUTimes parses /proc/stat: the "cpu" line (all cores) and "cpuN" lines.
// Columns: user nice system idle iowait irq softirq steal ...
func readCPUTimes() (all cpuTimes, cores []cpuTimes) {
	b, err := os.ReadFile("/proc/stat")
	if err != nil {
		return
	}
	for _, line := range strings.Split(string(b), "\n") {
		f := strings.Fields(line)
		if len(f) < 8 || !strings.HasPrefix(f[0], "cpu") {
			continue
		}
		var t cpuTimes
		for i, v := range f[1:] {
			n, _ := strconv.ParseUint(v, 10, 64)
			if i < 8 { // guest columns are already counted in user
				t.total += n
			}
			switch i {
			case 3, 4: // idle, iowait
				t.idle += n
			case 6:
				t.softirq = n
			}
		}
		if f[0] == "cpu" {
			all = t
		} else {
			cores = append(cores, t)
		}
	}
	return
}

// readProcTicks returns this process's user+system CPU time in clock ticks.
func readProcTicks() uint64 {
	b, err := os.ReadFile("/proc/self/stat")
	if err != nil {
		return 0
	}
	// The command name (field 2) is in parentheses and may contain spaces,
	// so count fields from after the closing parenthesis.
	s := string(b)
	i := strings.LastIndexByte(s, ')')
	if i < 0 {
		return 0
	}
	f := strings.Fields(s[i+1:])
	if len(f) < 13 {
		return 0
	}
	utime, _ := strconv.ParseUint(f[11], 10, 64)
	stime, _ := strconv.ParseUint(f[12], 10, 64)
	return utime + stime
}

func readKBField(path, key string) float64 {
	b, err := os.ReadFile(path)
	if err != nil {
		return 0
	}
	for _, line := range strings.Split(string(b), "\n") {
		if strings.HasPrefix(line, key) {
			f := strings.Fields(line[len(key):])
			if len(f) > 0 {
				v, _ := strconv.ParseFloat(f[0], 64)
				return v
			}
		}
	}
	return 0
}

func round1(v float64) float64 { return float64(int(v*10+0.5)) / 10 }
