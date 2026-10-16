// Package dispatch is the HTTP front of the bot service: the game server asks for a session, a goroutine plays it.
package dispatch

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"log/slog"
	"net/http"
	"sync"
	"sync/atomic"
	"time"

	"github.com/xuckless/tictactoeultimate/bots/internal/session"
)

// Server hands out personas to sessions; one goroutine per session, one game each.
type Server struct {
	Deps     session.Deps
	Token    string
	mu       sync.Mutex
	free     []session.Persona
	active   atomic.Int64
	played   atomic.Int64
	botWins  atomic.Int64
	botLoses atomic.Int64
	baseCtx  context.Context
}

// New server with the given personas.
func New(ctx context.Context, deps session.Deps, token string, personas []session.Persona) *Server {
	return &Server{Deps: deps, Token: token, free: append([]session.Persona(nil), personas...), baseCtx: ctx}
}

// Handler routes: POST /v1/sessions, GET /v1/stats, GET /healthz.
func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) { w.Write([]byte("ok")) })
	mux.HandleFunc("GET /v1/stats", s.stats)
	mux.HandleFunc("POST /v1/sessions", s.create)
	return mux
}

func (s *Server) authorized(r *http.Request) bool {
	want := "Bearer " + s.Token
	got := r.Header.Get("Authorization")
	return s.Token != "" && subtle.ConstantTimeCompare([]byte(got), []byte(want)) == 1
}

func (s *Server) create(w http.ResponseWriter, r *http.Request) {
	if !s.authorized(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var req session.Request
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil || req.TimeControl == "" {
		http.Error(w, "bad request", http.StatusBadRequest)
		return
	}
	p, ok := s.acquire()
	if !ok {
		http.Error(w, "no free bot persona", http.StatusServiceUnavailable)
		return
	}
	s.active.Add(1)
	go func() {
		defer s.release(p)
		defer s.active.Add(-1)
		ctx, cancel := context.WithTimeout(s.baseCtx, 2*time.Hour) // daily is excluded; live games are short
		defer cancel()
		res, err := session.Run(ctx, s.Deps, p, req)
		if err != nil {
			s.Deps.Log.Warn("session failed", "persona", p.Name, "err", err)
		}
		if res.Played {
			s.played.Add(1)
		}
		if res.BotWon {
			s.botWins.Add(1)
		}
		if res.BotLost {
			s.botLoses.Add(1)
		}
	}()
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusAccepted)
	_ = json.NewEncoder(w).Encode(map[string]string{"persona": p.Name, "requestId": req.RequestID})
}

func (s *Server) stats(w http.ResponseWriter, _ *http.Request) {
	s.mu.Lock()
	free := len(s.free)
	s.mu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]int64{
		"activeSessions": s.active.Load(), "freePersonas": int64(free), "gamesPlayed": s.played.Load(),
		"botWins": s.botWins.Load(), "botLosses": s.botLoses.Load(),
	})
}

func (s *Server) acquire() (session.Persona, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.free) == 0 {
		return session.Persona{}, false
	}
	i := int(time.Now().UnixNano() % int64(len(s.free))) // spread games over personas
	p := s.free[i]
	s.free = append(s.free[:i], s.free[i+1:]...)
	return p, true
}

func (s *Server) release(p session.Persona) {
	s.mu.Lock()
	s.free = append(s.free, p)
	s.mu.Unlock()
}

var _ = slog.Default
