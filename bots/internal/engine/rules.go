// Package engine is the Go port of TicTacToeUltimate.Engine's rules (docs/rules.md, ARCHITECTURE §4.2).
// It must agree with the C# engine on every case in spec/rules/vectors (see rules_test.go).
package engine

import (
	"fmt"
	"strconv"
	"strings"
)

// Color of a piece / player.
type Color uint8

const (
	X Color = iota
	O
)

// Other returns the opponent colour.
func (c Color) Other() Color { return 1 - c }

func (c Color) String() string {
	if c == X {
		return "X"
	}
	return "O"
}

// Removal kinds.
type Removal uint8

const (
	RemovalNone Removal = iota
	GlobalFifo
	PerPlayerCap
)

// Opening rules.
type Opening uint8

const (
	OpeningNone Opening = iota
	PieSwap
	BanCenter
)

// RuleSet mirrors the C# RuleSet record.
type RuleSet struct {
	N, K                 int
	Removal              Removal
	Window               int
	VacatedCellPlayable  bool
	VanishingPieceCanWin bool
	Opening              Opening
}

// Cells on the board.
func (r RuleSet) Cells() int { return r.N * r.N }

// ParseID parses "n{N}k{K}-{fifo{W}|cap{W}|none}-v{0|1}-w{0|1}-{none|pie|ban}".
func ParseID(id string) (RuleSet, error) {
	p := strings.Split(id, "-")
	if len(p) != 5 || !strings.HasPrefix(p[0], "n") {
		return RuleSet{}, fmt.Errorf("bad rules id %q", id)
	}
	ki := strings.Index(p[0], "k")
	if ki < 2 {
		return RuleSet{}, fmt.Errorf("bad rules id %q", id)
	}
	n, err1 := strconv.Atoi(p[0][1:ki])
	k, err2 := strconv.Atoi(p[0][ki+1:])
	if err1 != nil || err2 != nil {
		return RuleSet{}, fmt.Errorf("bad rules id %q", id)
	}
	r := RuleSet{N: n, K: k, Window: 7}
	switch {
	case p[1] == "none":
		r.Removal = RemovalNone
	case strings.HasPrefix(p[1], "fifo"):
		r.Removal = GlobalFifo
		r.Window, _ = strconv.Atoi(p[1][4:])
	case strings.HasPrefix(p[1], "cap"):
		r.Removal = PerPlayerCap
		r.Window, _ = strconv.Atoi(p[1][3:])
	default:
		return RuleSet{}, fmt.Errorf("bad removal in %q", id)
	}
	r.VacatedCellPlayable = p[2] == "v1"
	r.VanishingPieceCanWin = p[3] == "w1"
	switch p[4] {
	case "pie":
		r.Opening = PieSwap
	case "ban":
		r.Opening = BanCenter
	}
	return r, nil
}

// Piece on the board; Pieces are kept oldest first.
type Piece struct {
	Cell  int8
	Color Color
}

// Phase of a game.
type Phase uint8

const (
	AwaitingPlacement Phase = iota
	AwaitingSwapDecision
	Ended
)

// State is an immutable-by-convention game state.
type State struct {
	Rules      RuleSet
	Pieces     []Piece
	ToMove     Color
	Phase      Phase
	Placements int
	Ply        int
	Winner     int // -1 none, 0 X, 1 O, 2 draw
	Swapped    bool
}

// Initial empty-board state, X to move.
func Initial(r RuleSet) State { return State{Rules: r, ToMove: X, Winner: -1} }

// removalIndex: index in Pieces of the piece removed when `mover` places next, or -1.
func removalIndex(r RuleSet, pieces []Piece, mover Color) int {
	switch r.Removal {
	case GlobalFifo:
		if len(pieces) >= r.Window {
			return 0
		}
	case PerPlayerCap:
		count, first := 0, -1
		for i, p := range pieces {
			if p.Color == mover {
				if first < 0 {
					first = i
				}
				count++
			}
		}
		if count >= r.Window {
			return first
		}
	}
	return -1
}

// NextToVanish is the cell whose piece vanishes on the next placement, or -1.
func (s State) NextToVanish() int {
	if s.Phase != AwaitingPlacement {
		return -1
	}
	if i := removalIndex(s.Rules, s.Pieces, s.ToMove); i >= 0 {
		return int(s.Pieces[i].Cell)
	}
	return -1
}

func (s State) center(cell int) bool {
	n := s.Rules.N
	r, c := cell/n, cell%n
	if n%2 == 1 {
		return r == n/2 && c == n/2
	}
	return (r == n/2 || r == n/2-1) && (c == n/2 || c == n/2-1)
}

// Legal placement cells for the side to move (empty if not awaiting a placement).
func (s State) Legal() []int {
	if s.Phase != AwaitingPlacement {
		return nil
	}
	ri := removalIndex(s.Rules, s.Pieces, s.ToMove)
	occ := make([]bool, s.Rules.Cells())
	for i, p := range s.Pieces {
		if i != ri {
			occ[p.Cell] = true
		}
	}
	var out []int
	for c := 0; c < s.Rules.Cells(); c++ {
		if occ[c] {
			continue
		}
		if !s.Rules.VacatedCellPlayable && ri >= 0 && int(s.Pieces[ri].Cell) == c {
			continue
		}
		if s.Rules.Opening == BanCenter && s.Placements == 0 && s.center(c) {
			continue
		}
		out = append(out, c)
	}
	return out
}

// Lines for an N×N board with K in a row (row, column, diagonal, anti-diagonal order per start cell).
func Lines(n, k int) [][]int {
	var out [][]int
	dirs := [][2]int{{0, 1}, {1, 0}, {1, 1}, {1, -1}}
	for r := 0; r < n; r++ {
		for c := 0; c < n; c++ {
			for _, d := range dirs {
				var line []int
				ok := true
				for i := 0; i < k; i++ {
					rr, cc := r+d[0]*i, c+d[1]*i
					if rr < 0 || rr >= n || cc < 0 || cc >= n {
						ok = false
						break
					}
					line = append(line, rr*n+cc)
				}
				if ok {
					out = append(out, line)
				}
			}
		}
	}
	return out
}

var lineCache = map[[2]int][]uint16{}

func lineMasks(n, k int) []uint16 {
	key := [2]int{n, k}
	if m, ok := lineCache[key]; ok {
		return m
	}
	var masks []uint16
	for _, l := range Lines(n, k) {
		var m uint16
		for _, c := range l {
			m |= 1 << c
		}
		masks = append(masks, m)
	}
	lineCache[key] = masks
	return masks
}

// Place applies a placement; returns the new state and false if illegal.
func (s State) Place(cell int) (State, bool) {
	legal := false
	for _, c := range s.Legal() {
		if c == cell {
			legal = true
			break
		}
	}
	if !legal {
		return s, false
	}
	r := s.Rules
	mover := s.ToMove
	ri := removalIndex(r, s.Pieces, mover)
	np := make([]Piece, 0, len(s.Pieces)+1)
	for i, p := range s.Pieces {
		if i != ri {
			np = append(np, p)
		}
	}
	np = append(np, Piece{Cell: int8(cell), Color: mover})
	ns := s
	ns.Pieces = np
	ns.Placements++
	ns.Ply++
	// Win check for the mover (ghost rule excludes the mover's own next-to-vanish piece).
	ghost := -1
	if !r.VanishingPieceCanWin {
		if gi := removalIndex(r, np, mover.Other()); gi >= 0 && np[gi].Color == mover {
			ghost = int(np[gi].Cell)
		}
	}
	var mine uint16
	for _, p := range np {
		if p.Color == mover && int(p.Cell) != ghost {
			mine |= 1 << p.Cell
		}
	}
	for _, m := range lineMasks(r.N, r.K) {
		if mine&m == m {
			ns.Phase, ns.Winner = Ended, int(mover)
			return ns, true
		}
	}
	if r.Opening == PieSwap && ns.Placements == 1 {
		ns.ToMove, ns.Phase = O, AwaitingSwapDecision
		return ns, true
	}
	ns.ToMove = mover.Other()
	if len(ns.Legal()) == 0 {
		ns.Phase, ns.Winner = Ended, 2
	}
	return ns, true
}

// Decide applies the pie decision (swap=true swaps seats; colours on the board are unchanged and O places next).
func (s State) Decide(swap bool) (State, bool) {
	if s.Phase != AwaitingSwapDecision {
		return s, false
	}
	ns := s
	ns.Swapped = swap
	ns.Phase = AwaitingPlacement
	ns.Ply++
	return ns, true
}

// Board string (".XO", row-major).
func (s State) Board() string {
	b := []byte(strings.Repeat(".", s.Rules.Cells()))
	for _, p := range s.Pieces {
		b[p.Cell] = "XO"[p.Color]
	}
	return string(b)
}

// Replay applies notation actions ("4,0,swap,keep,…").
func Replay(r RuleSet, actions string) (State, error) {
	s := Initial(r)
	if strings.TrimSpace(actions) == "" {
		return s, nil
	}
	for i, a := range strings.Split(actions, ",") {
		a = strings.TrimSpace(a)
		var ok bool
		switch a {
		case "swap":
			s, ok = s.Decide(true)
		case "keep":
			s, ok = s.Decide(false)
		default:
			c, err := strconv.Atoi(a)
			if err != nil {
				return s, fmt.Errorf("bad action %q", a)
			}
			s, ok = s.Place(c)
		}
		if !ok {
			return s, fmt.Errorf("illegal action %d (%s)", i, a)
		}
	}
	return s, nil
}
