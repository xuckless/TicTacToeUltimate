package ops

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"
)

// Egress counts transmitted bytes per calendar month (UTC), persisted so restarts don't reset it.
type Egress struct {
	Month string `json:"month"`
	Bytes uint64 `json:"bytes"`
}

// Add counts bytes for a month, starting over when the month changes.
func (e *Egress) Add(n uint64, month string) {
	if e.Month != month {
		e.Month, e.Bytes = month, 0
	}
	e.Bytes += n
}

// Host mirrors HostStatsDto (admin contracts).
type Host struct {
	CPUPercent       float64 `json:"cpuPercent"`
	MemTotalBytes    uint64  `json:"memTotalBytes"`
	MemUsedBytes     uint64  `json:"memUsedBytes"`
	SwapUsedBytes    uint64  `json:"swapUsedBytes"`
	Load1            float64 `json:"load1"`
	DiskTotalBytes   uint64  `json:"diskTotalBytes"`
	DiskUsedBytes    uint64  `json:"diskUsedBytes"`
	RxBytesPerSec    uint64  `json:"rxBytesPerSec"`
	TxBytesPerSec    uint64  `json:"txBytesPerSec"`
	EgressMonthBytes uint64  `json:"egressMonthBytes"`
	UptimeSeconds    int64   `json:"uptimeSeconds"`
}

// Container mirrors ContainerStatsDto.
type Container struct {
	Name          string     `json:"name"`
	Image         string     `json:"image"`
	State         string     `json:"state"`
	CPUPercent    float64    `json:"cpuPercent"`
	MemBytes      uint64     `json:"memBytes"`
	MemLimitBytes uint64     `json:"memLimitBytes"`
	Restarts      int        `json:"restarts"`
	StartedAt     *time.Time `json:"startedAt,omitempty"`
}

// Sample mirrors InfraSampleDto.
type Sample struct {
	AtMs    int64   `json:"atMs"`
	Cpu     float64 `json:"cpu"`
	MemUsed uint64  `json:"memUsed"`
	Rx      uint64  `json:"rx"`
	Tx      uint64  `json:"tx"`
}

// LogLine mirrors LogEntryDto.
type LogLine struct {
	At      time.Time `json:"at"`
	Service string    `json:"service"`
	Level   string    `json:"level"`
	Text    string    `json:"text"`
}

// Collector samples the host every Interval into a 24 h ring.
type Collector struct {
	ProcRoot  string // host /proc mounted read-only, e.g. /host/proc
	DiskPath  string // any path on the host root filesystem (default /etc/hostname, bind-mounted from the host disk)
	StateFile string // where the egress counter lives
	Docker    *http.Client
	Interval  time.Duration
	mu        sync.Mutex
	host      Host
	ring      []Sample
	prevCPU   CPUTimes
	prevRx    uint64
	prevTx    uint64
	prevAt    time.Time
	egress    Egress
}

// NewCollector with the Docker API on a unix socket.
func NewCollector(procRoot, dockerSock, stateFile string) *Collector {
	c := &Collector{ProcRoot: procRoot, DiskPath: "/etc/hostname", StateFile: stateFile, Interval: 10 * time.Second}
	c.Docker = &http.Client{Timeout: 5 * time.Second, Transport: &http.Transport{DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
		return (&net.Dialer{}).DialContext(ctx, "unix", dockerSock)
	}}}
	if raw, err := os.ReadFile(stateFile); err == nil {
		_ = json.Unmarshal(raw, &c.egress)
	}
	return c
}

// Run samples until ctx ends.
func (c *Collector) Run(ctx context.Context) {
	t := time.NewTicker(c.Interval)
	defer t.Stop()
	for {
		c.sample()
		select {
		case <-ctx.Done():
			return
		case <-t.C:
		}
	}
}

func (c *Collector) open(name string) (io.ReadCloser, error) {
	return os.Open(filepath.Join(c.ProcRoot, name))
}

func (c *Collector) sample() {
	now := time.Now().UTC()
	var h Host
	if f, err := c.open("stat"); err == nil {
		if cur, ok := ParseStat(f); ok {
			if c.prevCPU.Total > 0 {
				h.CPUPercent = CPUPercent(c.prevCPU, cur)
			}
			c.prevCPU = cur
		}
		f.Close()
	}
	if f, err := c.open("meminfo"); err == nil {
		m := ParseMeminfo(f)
		f.Close()
		h.MemTotalBytes, h.MemUsedBytes, h.SwapUsedBytes = m.Total, m.Total-m.Available, m.SwapTotal-m.SwapFree
	}
	if f, err := c.open("loadavg"); err == nil {
		h.Load1 = ParseLoad(f)
		f.Close()
	}
	if f, err := c.open("uptime"); err == nil {
		h.UptimeSeconds = ParseUptime(f)
		f.Close()
	}
	var st syscall.Statfs_t
	if syscall.Statfs(c.DiskPath, &st) == nil {
		h.DiskTotalBytes = st.Blocks * uint64(st.Bsize)
		h.DiskUsedBytes = (st.Blocks - st.Bfree) * uint64(st.Bsize)
	}
	// /proc/1/net/dev of the host's /proc is the host network namespace (the container's own would only show its veth).
	if f, err := c.open("1/net/dev"); err == nil {
		rx, tx := NetBytes(f)
		f.Close()
		c.mu.Lock()
		if !c.prevAt.IsZero() && rx >= c.prevRx && tx >= c.prevTx {
			secs := uint64(now.Sub(c.prevAt).Seconds())
			if secs > 0 {
				h.RxBytesPerSec, h.TxBytesPerSec = (rx-c.prevRx)/secs, (tx-c.prevTx)/secs
			}
			c.egress.Add(tx-c.prevTx, now.Format("2006-01"))
		}
		c.prevRx, c.prevTx, c.prevAt = rx, tx, now
		c.mu.Unlock()
	}
	c.mu.Lock()
	h.EgressMonthBytes = c.egress.Bytes
	c.host = h
	c.ring = append(c.ring, Sample{AtMs: now.UnixMilli(), Cpu: h.CPUPercent, MemUsed: h.MemUsedBytes, Rx: h.RxBytesPerSec, Tx: h.TxBytesPerSec})
	if max := int(24 * time.Hour / c.Interval); len(c.ring) > max {
		c.ring = c.ring[len(c.ring)-max:]
	}
	state, _ := json.Marshal(c.egress)
	c.mu.Unlock()
	_ = os.WriteFile(c.StateFile, state, 0o600)
}

// Series returns the 24 h ring averaged into 5-minute buckets.
func (c *Collector) Series() []Sample {
	c.mu.Lock()
	defer c.mu.Unlock()
	var out []Sample
	var acc Sample
	n := 0
	for i, s := range c.ring {
		if n > 0 && s.AtMs/300000 != acc.AtMs/300000 {
			out = append(out, Sample{AtMs: acc.AtMs, Cpu: acc.Cpu / float64(n), MemUsed: acc.MemUsed / uint64(n), Rx: acc.Rx / uint64(n), Tx: acc.Tx / uint64(n)})
			acc, n = Sample{}, 0
		}
		if n == 0 {
			acc.AtMs = s.AtMs
		}
		acc.Cpu += s.Cpu
		acc.MemUsed += s.MemUsed
		acc.Rx += s.Rx
		acc.Tx += s.Tx
		n++
		if i == len(c.ring)-1 {
			out = append(out, Sample{AtMs: acc.AtMs, Cpu: acc.Cpu / float64(n), MemUsed: acc.MemUsed / uint64(n), Rx: acc.Rx / uint64(n), Tx: acc.Tx / uint64(n)})
		}
	}
	return out
}

// Host returns the latest host sample.
func (c *Collector) Host() Host { c.mu.Lock(); defer c.mu.Unlock(); return c.host }

type dockerContainer struct {
	ID     string            `json:"Id"`
	Names  []string          `json:"Names"`
	Image  string            `json:"Image"`
	State  string            `json:"State"`
	Labels map[string]string `json:"Labels"`
}

func (c *Collector) get(path string, v any) error {
	res, err := c.Docker.Get("http://docker" + path)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode != 200 {
		return fmt.Errorf("docker %s: %d", path, res.StatusCode)
	}
	return json.NewDecoder(res.Body).Decode(v)
}

// Containers lists containers with one-shot stats (CPU %, memory) and restart counts. Read-only Docker API calls only.
func (c *Collector) Containers() []Container {
	var list []dockerContainer
	if c.get("/containers/json?all=1", &list) != nil {
		return nil
	}
	out := make([]Container, 0, len(list))
	for _, d := range list {
		ct := Container{Name: serviceName(d), Image: d.Image, State: d.State}
		var insp struct {
			RestartCount int `json:"RestartCount"`
			State        struct {
				StartedAt time.Time `json:"StartedAt"`
			} `json:"State"`
		}
		if c.get("/containers/"+d.ID+"/json", &insp) == nil {
			ct.Restarts = insp.RestartCount
			if !insp.State.StartedAt.IsZero() {
				t := insp.State.StartedAt
				ct.StartedAt = &t
			}
		}
		if d.State == "running" {
			var s struct {
				CPU struct {
					Usage struct {
						Total uint64 `json:"total_usage"`
					} `json:"cpu_usage"`
					System uint64 `json:"system_cpu_usage"`
					Online int    `json:"online_cpus"`
				} `json:"cpu_stats"`
				PreCPU struct {
					Usage struct {
						Total uint64 `json:"total_usage"`
					} `json:"cpu_usage"`
					System uint64 `json:"system_cpu_usage"`
				} `json:"precpu_stats"`
				Mem struct {
					Usage uint64            `json:"usage"`
					Limit uint64            `json:"limit"`
					Stats map[string]uint64 `json:"stats"`
				} `json:"memory_stats"`
			}
			if c.get("/containers/"+d.ID+"/stats?stream=false", &s) == nil {
				if dSys := s.CPU.System - s.PreCPU.System; dSys > 0 && s.CPU.Usage.Total >= s.PreCPU.Usage.Total {
					online := s.CPU.Online
					if online == 0 {
						online = 1
					}
					ct.CPUPercent = float64(s.CPU.Usage.Total-s.PreCPU.Usage.Total) / float64(dSys) * float64(online) * 100
				}
				ct.MemBytes = s.Mem.Usage - s.Mem.Stats["inactive_file"]
				ct.MemLimitBytes = s.Mem.Limit
			}
		}
		out = append(out, ct)
	}
	return out
}

func serviceName(d dockerContainer) string {
	if s := d.Labels["com.docker.compose.service"]; s != "" {
		return s
	}
	if len(d.Names) > 0 {
		return strings.TrimPrefix(d.Names[0], "/")
	}
	return d.ID[:12]
}

// Logs returns the last lines of a compose service's container (stdout + stderr), newest last.
func (c *Collector) Logs(service string, tail int) []LogLine {
	var list []dockerContainer
	if c.get("/containers/json?all=1", &list) != nil {
		return nil
	}
	for _, d := range list {
		if serviceName(d) != service {
			continue
		}
		res, err := c.Docker.Get(fmt.Sprintf("http://docker/containers/%s/logs?stdout=1&stderr=1&timestamps=1&tail=%d", d.ID, tail))
		if err != nil {
			return nil
		}
		raw, _ := io.ReadAll(io.LimitReader(res.Body, 8<<20))
		res.Body.Close()
		var out []LogLine
		for _, line := range Demux(raw) {
			ts, text, _ := strings.Cut(line, " ")
			at, _ := time.Parse(time.RFC3339Nano, ts)
			out = append(out, LogLine{At: at, Service: service, Level: level(text), Text: text})
		}
		return out
	}
	return nil
}

func level(text string) string {
	l := strings.ToLower(text)
	switch {
	case strings.Contains(l, "error") || strings.Contains(l, "fail") || strings.Contains(l, "panic"):
		return "Error"
	case strings.Contains(l, "warn"):
		return "Warning"
	default:
		return "Information"
	}
}

// Demux splits Docker's multiplexed log stream (8-byte frame headers) into lines; plain text passes through.
func Demux(raw []byte) []string {
	var sb strings.Builder
	for len(raw) >= 8 && (raw[0] == 1 || raw[0] == 2) && raw[1] == 0 && raw[2] == 0 && raw[3] == 0 {
		n := int(binary.BigEndian.Uint32(raw[4:8]))
		if 8+n > len(raw) {
			break
		}
		sb.Write(raw[8 : 8+n])
		raw = raw[8+n:]
	}
	sb.Write(raw)
	var lines []string
	for _, l := range strings.Split(sb.String(), "\n") {
		if l = strings.TrimRight(l, "\r"); l != "" {
			lines = append(lines, l)
		}
	}
	return lines
}
