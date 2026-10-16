package brain

import (
	"math/rand/v2"
	"sync"

	"github.com/xuckless/tictactoeultimate/bots/internal/engine"
)

// Brain picks moves. Tablebases are solved lazily per ruleset and shared by all sessions.
type Brain struct {
	mu     sync.Mutex
	tables map[string]*engine.Tablebase
	solve  map[string]*sync.Once
}

// New brain.
func New() *Brain {
	return &Brain{tables: map[string]*engine.Tablebase{}, solve: map[string]*sync.Once{}}
}

// Table returns the solved tablebase for a ruleset id (3x3 only; nil otherwise).
func (b *Brain) Table(rulesID string, r engine.RuleSet) *engine.Tablebase {
	if r.N != 3 {
		return nil
	}
	key := rulesID
	b.mu.Lock()
	once, ok := b.solve[key]
	if !ok {
		once = &sync.Once{}
		b.solve[key] = once
	}
	b.mu.Unlock()
	once.Do(func() {
		tb := engine.Solve(r)
		b.mu.Lock()
		b.tables[key] = tb
		b.mu.Unlock()
	})
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.tables[key]
}

// Decision is either a placement or a pie decision.
type Decision struct {
	Cell int  // -1 for a pie decision
	Swap bool // only meaningful when Cell == -1
}

// ChooseClassic picks a move for a classic (non-Arena) position replayed from the server's action list.
// skill = probability of the perfect move; otherwise a plausible mistake.
func (b *Brain) ChooseClassic(rulesID string, s engine.State, skill float64, rng *rand.Rand) Decision {
	tb := b.Table(rulesID, s.Rules)
	if s.Phase == engine.AwaitingSwapDecision {
		keep, ok := lookup(tb, s, false)
		best := Decision{Cell: -1, Swap: ok && keep.Value == engine.Loss} // O to place loses -> take X's side
		if rng.Float64() < skill || !ok {
			return best
		}
		return Decision{Cell: -1, Swap: rng.IntN(2) == 0}
	}
	legal := s.Legal()
	if len(legal) == 0 {
		return Decision{Cell: -1}
	}
	type scored struct {
		cell int
		rank int // higher is better for the mover
	}
	var opts []scored
	for _, c := range legal {
		ns, _ := s.Place(c)
		rank := 0
		switch {
		case ns.Phase == engine.Ended && ns.Winner == int(s.ToMove):
			rank = 1_000_000
		case ns.Phase == engine.Ended:
			rank = 0
		default:
			if e, ok := lookup(tb, ns, false); ok {
				switch e.Value { // value is for the opponent (side to move after our placement)
				case engine.Loss:
					rank = 500_000 - e.Distance // we win: faster is better
				case engine.Draw:
					rank = 0
				case engine.Win:
					rank = -500_000 + e.Distance // we lose: resist longer
				}
			} else {
				rank = heuristic(s, c)
			}
		}
		opts = append(opts, scored{c, rank})
	}
	best := opts[0]
	for _, o := range opts[1:] {
		if o.rank > best.rank {
			best = o
		}
	}
	if rng.Float64() < skill || len(opts) == 1 {
		return Decision{Cell: best.cell}
	}
	// Mistake: any other legal move, never skipping an immediate win twice in a row is fine for humans too.
	var others []int
	for _, o := range opts {
		if o.cell != best.cell {
			others = append(others, o.cell)
		}
	}
	return Decision{Cell: others[rng.IntN(len(others))]}
}

// ChooseArena picks a move from the board for an Arena game (positions after a shatter are not in the classic table).
// Priority with skill: complete a line, block the opponent's line, centre, corners; mistakes are random legal cells.
func ChooseArena(n int, board string, legal []int, me engine.Color, myVanishing int, skill float64, rng *rand.Rand) int {
	if len(legal) == 0 {
		return -1
	}
	if rng.Float64() >= skill {
		return legal[rng.IntN(len(legal))]
	}
	mine, theirs := uint16(0), uint16(0)
	for i, ch := range board {
		switch {
		case ch == 'X' && me == engine.X, ch == 'O' && me == engine.O:
			if i != myVanishing {
				mine |= 1 << i
			}
		case ch != '.':
			theirs |= 1 << i
		}
	}
	lines := engine.Lines(n, 3)
	completes := func(set uint16, c int) bool {
		set |= 1 << c
		for _, l := range lines {
			full := true
			for _, x := range l {
				if set&(1<<x) == 0 {
					full = false
					break
				}
			}
			if full {
				return true
			}
		}
		return false
	}
	for _, c := range legal {
		if completes(mine, c) {
			return c
		}
	}
	for _, c := range legal {
		if completes(theirs, c) {
			return c
		}
	}
	prefer := []int{n * n / 2, 0, n - 1, n * (n - 1), n*n - 1}
	for _, p := range prefer {
		for _, c := range legal {
			if c == p {
				return c
			}
		}
	}
	return legal[rng.IntN(len(legal))]
}

func lookup(tb *engine.Tablebase, s engine.State, _ bool) (engine.Eval, bool) {
	if tb == nil {
		return engine.Eval{}, false
	}
	if s.Phase == engine.AwaitingSwapDecision {
		keep, _ := s.Decide(false)
		return tb.Lookup(keep)
	}
	return tb.Lookup(s)
}

// heuristic ranks a move without a table (bigger boards): centre-ish cells first.
func heuristic(s engine.State, c int) int {
	n := s.Rules.N
	r, col := c/n, c%n
	dr, dc := 2*r-(n-1), 2*col-(n-1)
	return -(dr*dr + dc*dc)
}
