# WP1 notes

- `w0` win check excludes the piece that vanishes on the **opponent's** next placement (NextToVanish with the opponent to move). Matches the study for FIFO; a no-op under PerPlayerCap and even FIFO windows.
- A won state keeps `ToMove` = winner (spec step 7 returns before step 9). `Tablebase.Evaluate` on Ended returns (Win,0)/(Loss,0) relative to `ToMove`.
- `NextToVanish` returns null once the game has ended.
- `Rules.ImmediateWins(state, c)` evaluates as if `c` were to place now (works for the non-moving colour too).
- `Tablebase.EvaluateMoves`: a placement that hands the chooser a pie decision is valued with the chooser taking the better side. Move classes are relative to the best available move (so pie openings classify sensibly even though `Evaluate` ignores pie).
- `NonTerminalCount` includes stuck (no-legal-move) positions, as the study does.
- `spec/rules/summary.json` is a hand-maintained oracle (study table); `gen` writes only `spec/rules/vectors/*.json` and `spec/clock/schedule.json`.
- Vector playouts are capped at 60 actions (w0 draws cycle forever).
- `TablebaseCache` is registered as singleton + `ITablebaseCache` + hosted service; builds run via `Task.Run` behind a 1-slot semaphore.
