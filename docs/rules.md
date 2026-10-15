# Rules (normative)

Implemented by `TicTacToeUltimate.Engine` (`Rules.Apply`). Any other engine (Go bots, clients) must reproduce `spec/rules/vectors/*.json` exactly.

## Ruleset

`RuleSet(BoardSize N, WinLength K, Removal, Window W, VacatedCellPlayable, VanishingPieceCanWin, Opening)`.
Canonical id: `n{N}k{K}-{fifo{W}|cap{W}|none}-v{0|1}-w{0|1}-{none|pie|ban}`.
Valid: 3 ≤ N ≤ 7, 3 ≤ K ≤ N; GlobalFifo 2K−1 ≤ W ≤ N²; PerPlayerCap K ≤ W ≤ ⌊N²/2⌋; None ignores W.

- **Default** `n3k3-cap3-v0-w1-none` (Casual, Unlimited, Challenge, Offline "Classic"; owner decision 2026-10-15 after playtesting): 3x3, 3 in a row, **each player holds at most 3 pieces**. Placing a 4th removes **your own oldest piece first**; you may not place on that just-vacated cell this turn; then **any 3-in-a-row of your visible pieces wins immediately** (no ghost exclusion).
- **Ranked/Daily** `n3k3-cap3-v0-w1-pie`: Default + pie rule.
- **Legacy** `n3k3-fifo7-v1-w0-none` (superseded "ghost" rule; still the Arena board and the Offline "Legacy 7-piece" preset): global FIFO of 7, the vacated cell is playable, the piece about to vanish cannot count toward a win. Replaced because a visible line through the mover's ghost did not win, which playtesters found confusing.

Cells are row-major `0..N²−1`. X places first. `Pieces` are kept oldest → newest.

## `Apply(state, action)`

Errors never change the state.

1. If `Phase == Ended`: `GameOver`.
2. **Swap phase** (`AwaitingSwapDecision`): only `Swap`/`KeepSides`, otherwise `WrongActionKind`. Then `Ply++`, `Phase = AwaitingPlacement`, `Swapped = (Swap)`. `ToMove` stays O and the board is unchanged. Continue at step 10.
3. In `AwaitingPlacement` only `Place` is allowed (`WrongActionKind`). A cell outside `[0, N²)` is `OutOfBounds`.
4. **Removal (before placement).** GlobalFifo: if `|Pieces| == W`, `removed = Pieces[0]`. PerPlayerCap: if the mover holds W pieces, `removed` = the mover's oldest piece. None: nothing.
5. **Legality** on the board without `removed`: occupied → `Occupied`; `!VacatedCellPlayable && cell == removed.Cell` → `CellJustVacated`; `Opening == BanCenter && Placements == 0` and the cell is a centre cell (odd N: the middle cell; even N: the central 2×2) → `BannedOpening`.
6. **Commit:** drop `removed`, append `Piece(cell, mover, Placements)`, `Placements++`, `Ply++`, `LastVanishedCell = removed?.Cell`.
7. **Win check (mover only).** Take the mover's pieces after the placement. If `!VanishingPieceCanWin`, exclude the **next piece to vanish** (the piece the removal rule of step 4 would remove when the *opponent* places next) when it belongs to the mover. If any line of `LineTable.For(N, K)` is fully covered, `Outcome = XWins/OWins`, `WinLine` = the first such line in LineTable order, `Phase = Ended`; `ToMove` stays the winner. Stop.
   - GlobalFifo, odd W: when the window is full the excluded piece is the mover's own oldest piece ("ghost"). With even W it is always the opponent's, so `w0` changes nothing.
   - PerPlayerCap: the next piece to vanish is the opponent's, so `w0` changes nothing.
8. **Pie trigger.** If `Opening == PieSwap && Placements == 1`: `ToMove = O`, `Phase = AwaitingSwapDecision`. Stop.
9. `ToMove` = the other colour.
10. **No legal move.** If `Phase == AwaitingPlacement` and `LegalCells` is empty: `Outcome = Draw`, `Phase = Ended`.

`LineTable.For(N, K)` order: for each start cell in row-major order, directions right, down, down-right, down-left; a line is listed if it fits.

`NextToVanish(state)`: the cell step 4 would remove when `ToMove` places next; null when none or when the game has ended.

`LegalActions`: Place actions in ascending cell order; `[Swap, KeepSides]` in the swap phase; empty when ended.

## Pie

After X's first placement, O chooses. **Swap**: the colours on the board stay, O is still to move, but the seats exchange colours (the first player now owns O and moves next). **Keep**: nothing changes. The decision is one ply.

## Notation

Actions joined by `,`: a cell number for Place, `swap`, `keep` (e.g. `4,swap,0`). Board string: N² chars `X`, `O`, `.`.

## Vector playouts

`SplitMix64(seed)`; repeat until ended or 60 actions: `legal = LegalActions(state)`, play `legal[rng.NextInt(legal.Length)]`. Seeds are decimal strings.

## Tablebase

Forward BFS over placement positions (pie ignored), retrograde labelling; positions never resolved are draws (perfect play cycles). Values are for the side to move; distance = plies to the forced end (winner fastest, loser slowest), 0 for draws and ended positions. `NonTerminalCount` counts every reachable position not already won (stuck positions included), as in the study.

Default oracle (`n3k3-cap3-v0-w1-none`): reachable 128,170, non-terminal 116,074; X wins in 13; edge openings win for X (O to move: Loss in 12); centre and corner openings are draws; with pie, perfect play is a draw. (Same values as the legacy `n3k3-fifo7-v1-w0-none` start/openings, but a different, smaller state space: 369,802 / 331,066.)

## UI hints (derived, client-side)

- The mover's piece that vanishes when they place (`NextToVanish`) is drawn faded + dashed, captioned "Your faded X vanishes when you place — you can't play there this turn". Clicking it explains why instead of failing silently.
- Under a per-player cap the waiting side's oldest piece (the one that vanishes on their next placement) gets a subtler ⏱ marker.
- Threat hints ("BLOCK IT!") use `Rules.ImmediateWins` on each position after the mover's possible placements, list only cells the mover can actually play, and are hidden when the mover can win immediately.
