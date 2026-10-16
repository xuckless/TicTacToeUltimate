// Package ops collects host and container metrics for the admin site's Infrastructure page (ttt-ops sidecar).
package ops

import (
	"bufio"
	"io"
	"strconv"
	"strings"
)

// CPUTimes is the aggregate "cpu" line of /proc/stat.
type CPUTimes struct{ Idle, Total uint64 }

// ParseStat reads the first line of /proc/stat.
func ParseStat(r io.Reader) (CPUTimes, bool) {
	s := bufio.NewScanner(r)
	for s.Scan() {
		f := strings.Fields(s.Text())
		if len(f) < 5 || f[0] != "cpu" {
			continue
		}
		var t CPUTimes
		for i, v := range f[1:] {
			n, _ := strconv.ParseUint(v, 10, 64)
			t.Total += n
			if i == 3 || i == 4 { // idle + iowait
				t.Idle += n
			}
		}
		return t, true
	}
	return CPUTimes{}, false
}

// CPUPercent between two samples.
func CPUPercent(a, b CPUTimes) float64 {
	if b.Total <= a.Total {
		return 0
	}
	busy := float64((b.Total - a.Total) - (b.Idle - a.Idle))
	return 100 * busy / float64(b.Total-a.Total)
}

// Mem holds the /proc/meminfo values we use, in bytes.
type Mem struct{ Total, Available, SwapTotal, SwapFree uint64 }

// ParseMeminfo reads /proc/meminfo.
func ParseMeminfo(r io.Reader) Mem {
	var m Mem
	s := bufio.NewScanner(r)
	for s.Scan() {
		f := strings.Fields(s.Text())
		if len(f) < 2 {
			continue
		}
		n, _ := strconv.ParseUint(f[1], 10, 64)
		n *= 1024
		switch strings.TrimSuffix(f[0], ":") {
		case "MemTotal":
			m.Total = n
		case "MemAvailable":
			m.Available = n
		case "SwapTotal":
			m.SwapTotal = n
		case "SwapFree":
			m.SwapFree = n
		}
	}
	return m
}

// NetBytes sums rx/tx over physical-looking interfaces of /proc/<pid>/net/dev (skips lo, docker, veth, bridges).
func NetBytes(r io.Reader) (rx, tx uint64) {
	s := bufio.NewScanner(r)
	for s.Scan() {
		line := s.Text()
		i := strings.Index(line, ":")
		if i < 0 {
			continue
		}
		name := strings.TrimSpace(line[:i])
		if name == "lo" || strings.HasPrefix(name, "docker") || strings.HasPrefix(name, "veth") || strings.HasPrefix(name, "br-") {
			continue
		}
		f := strings.Fields(line[i+1:])
		if len(f) < 9 {
			continue
		}
		r, _ := strconv.ParseUint(f[0], 10, 64)
		t, _ := strconv.ParseUint(f[8], 10, 64)
		rx += r
		tx += t
	}
	return
}

// ParseLoad returns the 1-minute load average from /proc/loadavg.
func ParseLoad(r io.Reader) float64 {
	b, _ := io.ReadAll(r)
	f := strings.Fields(string(b))
	if len(f) == 0 {
		return 0
	}
	v, _ := strconv.ParseFloat(f[0], 64)
	return v
}

// ParseUptime returns seconds from /proc/uptime.
func ParseUptime(r io.Reader) int64 {
	b, _ := io.ReadAll(r)
	f := strings.Fields(string(b))
	if len(f) == 0 {
		return 0
	}
	v, _ := strconv.ParseFloat(f[0], 64)
	return int64(v)
}
