package ops

import (
	"strings"
	"testing"
)

func TestParsers(t *testing.T) {
	a, ok := ParseStat(strings.NewReader("cpu  100 0 100 700 100 0 0 0 0 0\ncpu0 1 2 3 4\n"))
	if !ok || a.Total != 1000 || a.Idle != 800 {
		t.Fatalf("stat %+v", a)
	}
	b, _ := ParseStat(strings.NewReader("cpu  200 0 200 800 100 0 0 0 0 0\n"))
	if p := CPUPercent(a, b); p < 66 || p > 67 {
		t.Fatalf("cpu %% %v", p)
	}
	m := ParseMeminfo(strings.NewReader("MemTotal:        1000 kB\nMemFree: 1 kB\nMemAvailable:     400 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n"))
	if m.Total != 1000*1024 || m.Available != 400*1024 {
		t.Fatalf("mem %+v", m)
	}
	rx, tx := NetBytes(strings.NewReader(`Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo: 999 1 0 0 0 0 0 0 999 1 0 0 0 0 0 0
  ens4: 1000 10 0 0 0 0 0 0 2000 20 0 0 0 0 0 0
docker0: 5 1 0 0 0 0 0 0 5 1 0 0 0 0 0 0
veth12: 5 1 0 0 0 0 0 0 5 1 0 0 0 0 0 0
`))
	if rx != 1000 || tx != 2000 {
		t.Fatalf("net %d %d", rx, tx)
	}
	if l := ParseLoad(strings.NewReader("0.42 0.30 0.20 1/100 123")); l != 0.42 {
		t.Fatalf("load %v", l)
	}
	if u := ParseUptime(strings.NewReader("3600.5 100.0")); u != 3600 {
		t.Fatalf("uptime %v", u)
	}
}

func TestEgressRollsOverMonthly(t *testing.T) {
	e := &Egress{}
	e.Add(100, "2026-10")
	e.Add(50, "2026-10")
	if e.Bytes != 150 {
		t.Fatalf("got %d", e.Bytes)
	}
	e.Add(10, "2026-11")
	if e.Bytes != 10 || e.Month != "2026-11" {
		t.Fatalf("rollover %+v", e)
	}
}

func TestDemux(t *testing.T) {
	// Docker log stream framing: 8-byte header (stream, 0,0,0, big-endian length) then the payload.
	frame := func(s string) []byte { return append([]byte{1, 0, 0, 0, 0, 0, 0, byte(len(s))}, s...) }
	raw := append(frame("2026-10-16T10:00:00.000Z hello\n"), frame("2026-10-16T10:00:01.000Z warn: x\n")...)
	lines := Demux(raw)
	if len(lines) != 2 || lines[1] != "2026-10-16T10:00:01.000Z warn: x" {
		t.Fatalf("%q", lines)
	}
}
