//go:build !linux

package main

// System CPU/memory stats come from /proc, which only Linux has. Elsewhere
// (running on the Windows PC for development) they are simply left out.

type systemStats struct {
	Cores         int       `json:"cores"`
	CPUBusyPct    float64   `json:"cpuBusyPct"`
	CPUCoresPct   []float64 `json:"cpuCoresPct"`
	CPUSoftirqPct float64   `json:"cpuSoftirqPct"`
	ProcCPUPct    float64   `json:"procCpuPct"`
	RSSMB         float64   `json:"rssMB"`
	MemAvailMB    float64   `json:"memAvailMB"`
	Load1         float64   `json:"load1"`
}

type systemSampler struct{}

func newSystemSampler() *systemSampler { return &systemSampler{} }

func (s *systemSampler) sample() *systemStats { return nil }
