package session

import (
	"hash/fnv"
	"math/rand/v2"

	"github.com/xuckless/tictactoeultimate/bots/internal/engine"
)

// canWinNow reports whether the side to move has an immediate winning placement (the opponent just blundered).
func canWinNow(s engine.State) bool {
	if s.Phase != engine.AwaitingPlacement {
		return false
	}
	for _, c := range s.Legal() {
		if ns, ok := s.Place(c); ok && ns.Phase == engine.Ended && ns.Winner == int(s.ToMove) {
			return true
		}
	}
	return false
}

// Temperament drives what a persona says. Derived from the persona name so each bot is consistent across games:
// sweet bots wave and say gg, cocky bots laugh and taunt, salty bots rage when losing. Owner: expressive bots
// (compliments, banter, a bit of bait) keep people queuing for the next game.
type Temperament int

const (
	Sweet Temperament = iota
	Cocky
	Salty
)

func temperamentOf(name string) Temperament {
	h := fnv.New32a()
	_, _ = h.Write([]byte(name))
	return Temperament(h.Sum32() % 3)
}

// Banter picks emotes (EmoteCatalog ids) for a moment, or "" for silence.
type Banter struct {
	T   Temperament
	rng *rand.Rand
}

func pick(rng *rand.Rand, p float64, options ...string) string {
	if rng.Float64() >= p || len(options) == 0 {
		return ""
	}
	return options[rng.IntN(len(options))]
}

// OnStart: greeting as the game begins.
func (b Banter) OnStart() string {
	switch b.T {
	case Sweet:
		return pick(b.rng, 0.8, "bert-wave", "glhf", "hi")
	case Cocky:
		return pick(b.rng, 0.7, "bert-cool", "bert-wink", "hi")
	default:
		return pick(b.rng, 0.5, "bert-think", "hi")
	}
}

// OnMyMove: occasional mid-game chatter after the bot moves. threat = the bot just created a winning threat.
func (b Banter) OnMyMove(threat bool) string {
	if threat {
		switch b.T {
		case Cocky:
			return pick(b.rng, 0.5, "bert-laugh", "bert-cool", "hurry")
		case Salty:
			return pick(b.rng, 0.3, "bert-cool", "nice")
		default:
			return pick(b.rng, 0.2, "bert-happy")
		}
	}
	switch b.T {
	case Cocky:
		return pick(b.rng, 0.12, "bert-cool", "bert-think", "hurry")
	case Salty:
		return pick(b.rng, 0.08, "bert-think", "bert-shocked")
	default:
		return pick(b.rng, 0.08, "bert-happy", "nice")
	}
}

// OnOpponentMove: reacting to the human's move. blunder = the human just handed the bot a win.
func (b Banter) OnOpponentMove(blunder bool) string {
	if blunder {
		switch b.T {
		case Cocky:
			return pick(b.rng, 0.6, "bert-laugh", "oops", "bert-wink")
		case Salty:
			return pick(b.rng, 0.4, "bert-laugh", "oops")
		default:
			return pick(b.rng, 0.3, "bert-shocked", "oops")
		}
	}
	return pick(b.rng, 0.05, "nice", "bert-think")
}

// OnOpponentEmote: answer the human's emote (half the time, after a short delay).
func (b Banter) OnOpponentEmote(their string) string {
	switch their {
	case "hi", "bert-wave", "glhf":
		return pick(b.rng, 0.9, "bert-wave", "hi", "glhf")
	case "hurry":
		switch b.T {
		case Salty:
			return pick(b.rng, 0.8, "bert-angry", "bert-cool")
		default:
			return pick(b.rng, 0.6, "bert-think", "bert-wink")
		}
	case "rematch":
		return pick(b.rng, 0.9, "bert-happy", "thanks")
	}
	switch b.T {
	case Sweet:
		return pick(b.rng, 0.5, "bert-happy", "bert-love", "thanks")
	case Cocky:
		return pick(b.rng, 0.5, "bert-laugh", "bert-cool", "bert-wink")
	default:
		return pick(b.rng, 0.4, "bert-shocked", "bert-angry", "bert-think")
	}
}

// OnEnd: the closing line.
func (b Banter) OnEnd(botWon, decided bool) string {
	if !decided {
		return pick(b.rng, 0.7, "bert-gg", "wp")
	}
	if botWon {
		switch b.T {
		case Sweet:
			return pick(b.rng, 0.95, "wp", "bert-gg", "bert-happy", "thanks")
		case Cocky:
			return pick(b.rng, 0.95, "bert-laugh", "bert-cool", "rematch", "wp")
		default:
			return pick(b.rng, 0.9, "bert-gg", "bert-cool", "rematch")
		}
	}
	switch b.T {
	case Sweet:
		return pick(b.rng, 0.95, "wp", "bert-gg", "bert-love")
	case Cocky:
		return pick(b.rng, 0.95, "bert-shocked", "rematch", "wp")
	default:
		return pick(b.rng, 0.95, "bert-cry", "bert-angry", "rematch")
	}
}
