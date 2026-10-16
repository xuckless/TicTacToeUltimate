package engine

// Retrograde tablebase for small rulesets (3x3): every reachable position from the empty board is solved to
// Win / Loss / Draw (draw = perfect play cycles forever) with a distance to the forced end. Pie decisions are not
// positions in the table (the swap value is the negation of the keep value).

// Value of a position for the side to move.
type Value int8

const (
	Draw Value = iota
	Win
	Loss
)

func (v Value) String() string { return [...]string{"Draw", "Win", "Loss"}[v] }

// Eval is a solved value with distance (plies to the forced end; 0 for draws).
type Eval struct {
	Value    Value
	Distance int
}

// Key canonically identifies a position (placement phase only).
// PerPlayerCap: order only matters within each colour, so pieces are encoded per colour; otherwise the global order.
type Key [2]uint64

// KeyOf builds the canonical key of s.
func KeyOf(s State) Key {
	var a, b uint64
	if s.Rules.Removal == PerPlayerCap {
		for _, p := range s.Pieces {
			if p.Color == X {
				a = a<<5 | uint64(p.Cell+1)
			} else {
				b = b<<5 | uint64(p.Cell+1)
			}
		}
	} else {
		for _, p := range s.Pieces {
			a = a<<5 | uint64(p.Cell+1) | uint64(p.Color)<<4
			if a>>58 != 0 { // overflow guard for long queues: spill into b
				b = b<<5 ^ a>>58
				a &= (1 << 58) - 1
			}
		}
	}
	return Key{a<<1 | uint64(s.ToMove), b}
}

// Tablebase holds solved values.
type Tablebase struct {
	Rules       RuleSet
	vals        map[Key]Eval
	Reachable   int
	NonTerminal int
}

// Solve builds the tablebase for r (pie/ban openings are solved on the underlying placement game).
func Solve(r RuleSet) *Tablebase {
	base := r
	base.Opening = OpeningNone
	if r.Opening == BanCenter {
		base.Opening = BanCenter
	}
	type node struct {
		s        State
		children []Key
		terminal bool
	}
	nodes := map[Key]*node{}
	parents := map[Key][]Key{}
	start := Initial(base)
	queue := []State{start}
	nodes[KeyOf(start)] = &node{s: start}
	for len(queue) > 0 {
		s := queue[0]
		queue = queue[1:]
		k := KeyOf(s)
		n := nodes[k]
		if s.Phase == Ended {
			n.terminal = true
			continue
		}
		for _, c := range s.Legal() {
			ns, _ := s.Place(c)
			if ns.Phase == Ended && ns.Winner != 2 {
				ns.ToMove = Color(ns.Winner).Other() // the side to move in a won position is the loser
			}
			ck := KeyOf(ns)
			if _, ok := nodes[ck]; !ok {
				nodes[ck] = &node{s: ns}
				queue = append(queue, ns)
			}
			n.children = append(n.children, ck)
			parents[ck] = append(parents[ck], k)
		}
	}
	tb := &Tablebase{Rules: r, vals: make(map[Key]Eval, len(nodes)), Reachable: len(nodes)}
	remaining := map[Key]int{}
	var frontier []Key
	for k, n := range nodes {
		if n.terminal {
			if n.s.Winner == 2 {
				tb.vals[k] = Eval{Draw, 0}
				tb.NonTerminal++ // "stuck" (no legal move) positions count as non-terminal, matching the C# tablebase
			} else {
				tb.vals[k] = Eval{Loss, 0}
				frontier = append(frontier, k)
			}
		} else {
			tb.NonTerminal++
			remaining[k] = len(n.children)
		}
	}
	// Retrograde BFS by distance: a parent is Win if any child is Loss; Loss if all children are Win.
	for len(frontier) > 0 {
		var next []Key
		for _, ck := range frontier {
			cv := tb.vals[ck]
			for _, pk := range parents[ck] {
				if _, done := tb.vals[pk]; done {
					continue
				}
				if cv.Value == Loss {
					tb.vals[pk] = Eval{Win, cv.Distance + 1}
					next = append(next, pk)
				} else if cv.Value == Win {
					remaining[pk]--
					if remaining[pk] == 0 {
						// Loss: the longest resistance among (all winning-for-opponent) children.
						best := 0
						for _, c2 := range nodes[pk].children {
							if d := tb.vals[c2].Distance; d > best {
								best = d
							}
						}
						tb.vals[pk] = Eval{Loss, best + 1}
						next = append(next, pk)
					}
				}
			}
		}
		frontier = next
	}
	for k := range nodes {
		if _, ok := tb.vals[k]; !ok {
			tb.vals[k] = Eval{Draw, 0}
		}
	}
	return tb
}

// Lookup returns the solved value of s for the side to move (ok=false if the position isn't in the table,
// e.g. an Arena position after a shatter).
func (tb *Tablebase) Lookup(s State) (Eval, bool) {
	e, ok := tb.vals[KeyOf(s)]
	return e, ok
}
