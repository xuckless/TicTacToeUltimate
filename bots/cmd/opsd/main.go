// Command opsd is the ttt-ops sidecar: host and container metrics plus container logs for the admin site, on the
// private compose network only (never exposed by Caddy). Every request needs Authorization: Bearer $OPS_TOKEN.
package main

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	"github.com/xuckless/tictactoeultimate/bots/internal/ops"
)

func env(k, d string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return d
}

func main() {
	token := os.Getenv("OPS_TOKEN")
	if token == "" {
		slog.Error("OPS_TOKEN is required")
		os.Exit(1)
	}
	c := ops.NewCollector(env("OPS_PROC", "/host/proc"), env("OPS_DOCKER_SOCK", "/var/run/docker.sock"), env("OPS_STATE", "/data/egress.json"))
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	go c.Run(ctx)

	auth := func(h http.HandlerFunc) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			if subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), []byte("Bearer "+token)) != 1 {
				http.Error(w, "unauthorized", http.StatusUnauthorized)
				return
			}
			h(w, r)
		}
	}
	write := func(w http.ResponseWriter, v any) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(v)
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("ok")) })
	mux.HandleFunc("GET /v1/stats", auth(func(w http.ResponseWriter, _ *http.Request) {
		h := c.Host()
		write(w, map[string]any{"host": h, "containers": c.Containers(), "series": c.Series()})
	}))
	mux.HandleFunc("GET /v1/logs", auth(func(w http.ResponseWriter, r *http.Request) {
		tail, _ := strconv.Atoi(r.URL.Query().Get("tail"))
		if tail <= 0 || tail > 2000 {
			tail = 500
		}
		write(w, c.Logs(r.URL.Query().Get("service"), tail))
	}))
	srv := &http.Server{Addr: ":" + env("PORT", "8090"), Handler: mux, ReadHeaderTimeout: 5 * time.Second}
	go func() { <-ctx.Done(); _ = srv.Shutdown(context.Background()) }()
	slog.Info("ttt-ops listening", "addr", srv.Addr)
	if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		slog.Error("server", "err", err)
		os.Exit(1)
	}
}
