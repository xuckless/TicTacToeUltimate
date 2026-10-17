// Package session runs one bot game: connect over /bot/ws as a persona, seek one time control, play, report, exit.
package session

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"math/rand/v2"
	"net/http"
	"strings"
	"time"

	"github.com/coder/websocket"
	"github.com/xuckless/tictactoeultimate/bots/internal/brain"
	"github.com/xuckless/tictactoeultimate/bots/internal/engine"
	"github.com/xuckless/tictactoeultimate/bots/internal/protocol"
)

// Request describes the session the game server asked for.
type Request struct {
	TimeControl    string `json:"timeControl"`
	OpponentUserID string `json:"opponentUserId"`
	OpponentRating int    `json:"opponentRating"`
	RequestID      string `json:"requestId"`
}

// Persona is a bot identity (one bot API key = one bot account with a human-like name).
type Persona struct {
	Name string `json:"name"`
	Key  string `json:"key"`
}

// Deps shared by all sessions.
type Deps struct {
	WSURL       string // ws://host:5091/bot/ws?protocol=1
	Brain       *brain.Brain
	Tracker     *brain.Tracker
	Log         *slog.Logger
	SeekTimeout time.Duration
}

// Result of a finished session.
type Result struct {
	Played, BotWon, BotLost bool
}

// Run plays exactly one game (or gives up after SeekTimeout without a match). It never panics on bad input.
func Run(ctx context.Context, d Deps, p Persona, req Request) (res Result, err error) {
	log := d.Log.With("persona", p.Name, "tc", req.TimeControl, "req", req.RequestID)
	hdr := http.Header{}
	hdr.Set("Authorization", "Bearer "+p.Key)
	conn, _, err := websocket.Dial(ctx, d.WSURL, &websocket.DialOptions{HTTPHeader: hdr})
	if err != nil {
		return res, fmt.Errorf("dial: %w", err)
	}
	defer conn.CloseNow()
	conn.SetReadLimit(64 << 10)
	send := func(v any) error {
		raw, _ := json.Marshal(v)
		wctx, cancel := context.WithTimeout(ctx, 5*time.Second)
		defer cancel()
		return conn.Write(wctx, websocket.MessageText, raw)
	}
	if err := send(protocol.Seek{Type: "seek", ID: "s1", TimeControls: []string{req.TimeControl}, MaxConcurrentGames: 1}); err != nil {
		return res, err
	}
	rng := rand.New(rand.NewPCG(uint64(time.Now().UnixNano()), uint64(len(p.Name))))
	banter := Banter{T: temperamentOf(p.Name), rng: rng}
	emoteN := 0
	emote := func(matchID, id string, delay time.Duration) {
		if id == "" {
			return
		}
		emoteN++
		msg := protocol.Emote{Type: "emote", ID: fmt.Sprintf("e-%d", emoteN), MatchID: matchID, EmoteID: id}
		if delay <= 0 {
			_ = send(msg)
			return
		}
		go func() {
			select {
			case <-time.After(delay):
				_ = send(msg)
			case <-ctx.Done():
			}
		}()
	}
	seekDeadline := time.Now().Add(d.SeekTimeout)
	var (
		game     *protocol.GameStart
		rules    engine.RuleSet
		arena    bool
		skill    float64
		opponent string
		shadow   bool
		actionN  int
	)
	for {
		rctx, cancel := context.WithTimeout(ctx, 30*time.Second)
		if game == nil {
			rctx, cancel = context.WithDeadline(ctx, seekDeadline)
		}
		_, raw, err := conn.Read(rctx)
		cancel()
		if err != nil {
			if game == nil {
				_ = send(protocol.Simple{Type: "unseek", ID: "s2"})
				log.Info("no match before the seek timeout; leaving")
				conn.Close(websocket.StatusNormalClosure, "seek timeout")
				return res, nil
			}
			return res, fmt.Errorf("read: %w", err)
		}
		var env protocol.Envelope
		if json.Unmarshal(raw, &env) != nil {
			continue
		}
		switch env.Type {
		case "ping":
			var pg protocol.Ping
			_ = json.Unmarshal(raw, &pg)
			_ = send(protocol.Simple{Type: "pong", T: pg.T})
		case "gameStart":
			var gs protocol.GameStart
			if json.Unmarshal(raw, &gs) != nil {
				continue
			}
			game = &gs
			rules, err = engine.ParseID(gs.Rules.ID)
			if err != nil {
				rules = engine.RuleSet{N: 3, K: 3}
			}
			arena = strings.HasPrefix(gs.Mode, "Arena") || strings.HasPrefix(gs.TimeControl, "Arena")
			opponent = gs.Opponent.UserID
			rating := req.OpponentRating
			if gs.Opponent.Rating != nil {
				rating = *gs.Opponent.Rating
			}
			shadow = gs.OpponentShadow
			skill = d.Tracker.SkillFor(opponent, rating, shadow)
			_ = send(protocol.Simple{Type: "unseek", ID: "s2"}) // one game per session
			log.Info("game start", "match", gs.MatchID, "vs", gs.Opponent.DisplayName, "rating", rating, "skill", fmt.Sprintf("%.2f", skill), "arena", arena)
			emote(gs.MatchID, banter.OnStart(), time.Duration(800+rng.IntN(1500))*time.Millisecond)
		case "opponentEmote":
			var oe protocol.OpponentEmote
			if json.Unmarshal(raw, &oe) == nil && game != nil {
				emote(oe.MatchID, banter.OnOpponentEmote(oe.EmoteID), time.Duration(1200+rng.IntN(2000))*time.Millisecond)
			}
		case "state":
			var st protocol.State
			if json.Unmarshal(raw, &st) != nil || game == nil || !st.YourTurn || st.Phase == "Ended" {
				continue
			}
			if !arena && st.Ply > 0 {
				// The human just moved: did they hand us a win? (Perfect-play check against the tablebase, independent of our skill dial.)
				if s, err := engine.Replay(rules, st.Actions); err == nil {
					emote(st.MatchID, banter.OnOpponentMove(canWinNow(s)), time.Duration(600+rng.IntN(900))*time.Millisecond)
				}
			}
			think(ctx, st, arena, rng)
			actionN++
			act := protocol.Action{Type: "action", ID: fmt.Sprintf("m-%d", actionN), MatchID: st.MatchID, Ply: st.Ply}
			if st.Phase == "AwaitingSwapDecision" {
				s, _ := engine.Replay(rules, st.Actions)
				dcs := d.Brain.ChooseClassic(game.Rules.ID, s, skill, rng)
				act.Kind = map[bool]string{true: "Swap", false: "KeepSides"}[dcs.Swap]
			} else {
				cell := chooseCell(d, game.Rules.ID, rules, st, arena, skill, rng)
				if cell < 0 {
					continue
				}
				act.Kind, act.Cell = "Place", &cell
			}
			if err := send(act); err != nil {
				return res, err
			}
			if act.Kind == "Place" {
				emote(st.MatchID, banter.OnMyMove(false), time.Duration(900+rng.IntN(1500))*time.Millisecond)
			}
		case "gameEnd":
			var ge protocol.GameEnd
			_ = json.Unmarshal(raw, &ge)
			res.Played = true
			decided := ge.Outcome == "XWins" || ge.Outcome == "OWins"
			res.BotWon = decided && ge.YouWon
			res.BotLost = decided && !ge.YouWon
			d.Tracker.RecordFor(opponent, res.BotWon, res.BotLost, shadow)
			log.Info("game end", "outcome", ge.Outcome, "reason", ge.Reason, "botWon", ge.YouWon)
			// The match actor lingers after the end, so a closing emote lands on the result screen.
			if id := banter.OnEnd(res.BotWon, decided); id != "" {
				time.Sleep(time.Duration(700+rng.IntN(1200)) * time.Millisecond)
				emote(ge.MatchID, id, 0)
				time.Sleep(300 * time.Millisecond)
			}
			conn.Close(websocket.StatusNormalClosure, "game over")
			return res, nil
		case "error":
			var e protocol.Error
			_ = json.Unmarshal(raw, &e)
			log.Warn("server error", "code", e.Code, "msg", e.Message)
		}
	}
}

func chooseCell(d Deps, rulesID string, rules engine.RuleSet, st protocol.State, arena bool, skill float64, rng *rand.Rand) int {
	if len(st.Legal) == 0 {
		return -1
	}
	me := engine.X
	if st.You == "O" {
		me = engine.O
	}
	if arena {
		v := -1
		if st.NextToVanish != nil {
			v = *st.NextToVanish
		}
		return brain.ChooseArena(rules.N, st.Board, st.Legal, me, v, skill, rng)
	}
	s, err := engine.Replay(rules, st.Actions)
	if err != nil || s.Board() != st.Board {
		// Out of sync with the server: stay legal.
		return st.Legal[rng.IntN(len(st.Legal))]
	}
	c := d.Brain.ChooseClassic(rulesID, s, skill, rng).Cell
	for _, l := range st.Legal {
		if l == c {
			return c
		}
	}
	return st.Legal[rng.IntN(len(st.Legal))]
}

// think waits like a person would: a share of the move allowance (quicker in Arena and sudden death).
func think(ctx context.Context, st protocol.State, arena bool, rng *rand.Rand) {
	lo, hi := 700.0, 2800.0
	if st.AllowanceMs != nil {
		a := float64(*st.AllowanceMs)
		lo, hi = min(lo, a*0.12), min(hi, a*0.42)
	}
	if arena {
		hi = min(hi, 1900)
	}
	if hi < lo {
		hi = lo
	}
	delay := time.Duration(lo+rng.Float64()*(hi-lo)) * time.Millisecond
	select {
	case <-ctx.Done():
	case <-time.After(delay):
	}
}
