package brain

import (
	"math/rand/v2"
	"testing"

	"github.com/xuckless/tictactoeultimate/bots/internal/engine"
)

// A perfect bot never loses a classic game against a random opponent from the empty board.
func TestPerfectBotNeverLosesToRandom(t *testing.T) {
	b := New()
	id := "n3k3-cap3-v0-w1-none"
	r, _ := engine.ParseID(id)
	rng := rand.New(rand.NewPCG(1, 2))
	for g := 0; g < 200; g++ {
		s := engine.Initial(r)
		botColor := engine.Color(g % 2)
		for ply := 0; ply < 80 && s.Phase != engine.Ended; ply++ {
			var c int
			if s.ToMove == botColor {
				c = b.ChooseClassic(id, s, 1.0, rng).Cell
			} else {
				l := s.Legal()
				c = l[rng.IntN(len(l))]
			}
			s, _ = s.Place(c)
		}
		if s.Phase == engine.Ended && s.Winner == int(botColor.Other()) {
			t.Fatalf("perfect bot lost game %d", g)
		}
	}
}

// Against simulated opponents of very different strength, the adaptive staircase keeps the bot's share of decided
// games near 49%. Opponents play the perfect move with probability q, else a random move. Undecided games (cycles that a
// real clock would settle) are ignored, as the live service only records decided games.
func TestAdaptiveWinRateNear49(t *testing.T) {
	b := New()
	id := "n3k3-cap3-v0-w1-none"
	r, _ := engine.ParseID(id)
	for _, q := range []float64{0.35, 0.65, 0.9} {
		tr := NewTracker("")
		rng := rand.New(rand.NewPCG(uint64(q*1000), 7))
		wins, losses := 0, 0
		for g := 0; g < 3000; g++ {
			s := engine.Initial(r)
			botColor := engine.Color(g % 2)
			skill := tr.Skill("opp", 1500)
			for ply := 0; ply < 60 && s.Phase != engine.Ended; ply++ {
				p := skill
				if s.ToMove != botColor {
					p = q
				}
				s, _ = s.Place(b.ChooseClassic(id, s, p, rng).Cell)
			}
			if s.Phase != engine.Ended || s.Winner == 2 {
				continue
			}
			won := s.Winner == int(botColor)
			tr.Record("opp", won, !won)
			if g >= 1000 { // after warm-up
				if won {
					wins++
				} else {
					losses++
				}
			}
		}
		rate := float64(wins) / float64(wins+losses)
		t.Logf("opponent q=%.2f: bot won %.1f%% of %d decided games", q, rate*100, wins+losses)
		if rate < 0.44 || rate > 0.54 {
			t.Errorf("q=%.2f: bot win rate %.3f not near 0.49", q, rate)
		}
	}
}

// Shadow pool: against a perfect (engine-assisted) player the full-strength profile keeps the player's share of decided
// games inside 40-70% (owner decision 2026-10-16), i.e. the bot wins 30-60%.
func TestShadowProfileHoldsCheatersInBand(t *testing.T) {
	b := New()
	id := "n3k3-cap3-v0-w1-none"
	r, _ := engine.ParseID(id)
	tr := NewTracker("")
	rng := rand.New(rand.NewPCG(42, 9))
	wins, losses := 0, 0
	for g := 0; g < 1500; g++ {
		s := engine.Initial(r)
		botColor := engine.Color(g % 2)
		skill := tr.SkillFor("cheater", 1500, true)
		for ply := 0; ply < 60 && s.Phase != engine.Ended; ply++ {
			p := skill
			if s.ToMove != botColor {
				p = 1.0
			}
			s, _ = s.Place(b.ChooseClassic(id, s, p, rng).Cell)
		}
		if s.Phase != engine.Ended || s.Winner == 2 {
			continue
		}
		won := s.Winner == int(botColor)
		tr.RecordFor("cheater", won, !won, true)
		if g >= 300 {
			if won {
				wins++
			} else {
				losses++
			}
		}
	}
	human := float64(losses) / float64(wins+losses)
	t.Logf("perfect player won %.1f%% of %d decided games against the shadow profile", human*100, wins+losses)
	if human < 0.40 || human > 0.70 {
		t.Errorf("perfect player win rate %.3f outside 0.40-0.70", human)
	}
}
