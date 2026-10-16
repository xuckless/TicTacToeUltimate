// botd: the TTT Ultimate bot service. The game server POSTs /v1/sessions when a player has waited without a human
// opponent; botd spawns a goroutine that connects over /bot/ws as a human-named persona and plays one game, with
// difficulty tuned so bots win ≈49% against each opponent.
//
// Env: TTT_BOT_LISTEN (:7070), TTT_SERVER_WS (ws://localhost:5091/bot/ws?protocol=1), TTT_DISPATCH_TOKEN,
// TTT_BOT_PERSONAS (config/personas.dev.json), TTT_BOT_STATE (difficulty offsets file, optional), TTT_SEEK_TIMEOUT (45s).
package main

import (
	"context"
	"encoding/json"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/xuckless/tictactoeultimate/bots/internal/brain"
	"github.com/xuckless/tictactoeultimate/bots/internal/dispatch"
	"github.com/xuckless/tictactoeultimate/bots/internal/engine"
	"github.com/xuckless/tictactoeultimate/bots/internal/session"
)

func env(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func main() {
	log := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	raw, err := os.ReadFile(env("TTT_BOT_PERSONAS", "config/personas.dev.json"))
	if err != nil {
		log.Error("personas", "err", err)
		os.Exit(1)
	}
	var personas []session.Persona
	if err := json.Unmarshal(raw, &personas); err != nil || len(personas) == 0 {
		log.Error("personas file is empty or invalid", "err", err)
		os.Exit(1)
	}
	seek, err := time.ParseDuration(env("TTT_SEEK_TIMEOUT", "45s"))
	if err != nil {
		seek = 45 * time.Second
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	b := brain.New()
	go b.Table("n3k3-cap3-v0-w1-none", mustRules("n3k3-cap3-v0-w1-none")) // warm the main table
	deps := session.Deps{WSURL: env("TTT_SERVER_WS", "ws://localhost:5091/bot/ws?protocol=1"), Brain: b,
		Tracker: brain.NewTracker(os.Getenv("TTT_BOT_STATE")), Log: log, SeekTimeout: seek}
	srv := dispatch.New(ctx, deps, env("TTT_DISPATCH_TOKEN", "dev-bot-dispatch-token"), personas)
	hs := &http.Server{Addr: env("TTT_BOT_LISTEN", ":7070"), Handler: srv.Handler(), ReadHeaderTimeout: 5 * time.Second}
	go func() {
		<-ctx.Done()
		sh, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = hs.Shutdown(sh)
	}()
	log.Info("botd listening", "addr", hs.Addr, "personas", len(personas), "server", deps.WSURL)
	if err := hs.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		log.Error("listen", "err", err)
		os.Exit(1)
	}
}

func mustRules(id string) engine.RuleSet { r, _ := engine.ParseID(id); return r }
