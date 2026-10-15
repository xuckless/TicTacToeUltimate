# Arena (Health) Mode: Design Recommendation

**Summary:** the health-based idea works, but not with the first-draft rules. With 100 HP, 25 damage per line and a 2:00 round, 46–71% of skilled games end level and go to sudden death. Two rule changes and a slightly larger HP pool fix most of that, and the stronger player still wins clearly.

## 1. Recommended ruleset (ruleset C, 125 HP)

| Setting | Value |
|---|---|
| Board | 3x3, rolling window W=7, ghost rule on |
| HP | **125** each |
| Line damage | 25 → 35 → 50 (combo steps, capped at 50) |
| Combo | **Unanswered combo:** your streak survives your own turns where you don't score. It resets only when the opponent scores. |
| Crit (2+ lines in one move) | lines × base damage + 15 |
| Red alert | **Double damage in the last 30 s** of normal time |
| Round | 2:00 |
| Time-out | Equal turns: time-out is checked only after O moves |
| KO | Checked before time-out. A KO ends the game at once. |
| Overtime | Sudden death: the next line wins |
| Pie rule | Not needed |
| Optional | Start overtime with the player who conceded the last line. This removes X's 52–61% overtime edge. |

**More skill-driven alternative (ruleset B):** the first-draft rules plus the unanswered combo only. Stronger players win 65–80%, but there is more overtime (26–46%) and no comebacks from two lines down.

## 2. What changes from the base game

- **Health instead of a single win:** completing a line damages the opponent instead of ending the game.
- **Shatter:** the completed line's pieces are removed from the board and from the queue. The rest of the queue keeps its order, and the emptied cells can be played again.
- **Clock-based ending:** HP decides the game at 2:00, with equal turns, then sudden death if HP is level.
- **New mechanics:** combo, crit and red alert.
- **Board and window are unchanged:** W=7 and the ghost rule stay. Average occupancy drops from about 6.3 pieces to 5.2–5.5. Only 1–2% of shatters leave 1 or fewer pieces, so the board doesn't become too empty to play.

## 3. Bonus mechanics: what works and what doesn't

| Mechanic | Verdict | Fix |
|---|---|---|
| **Combo ramp (25→35→50), first draft** | **Doesn't work.** The streak resets on any blank turn, so it almost never builds. The same player scores twice in a row only 0.06–1.2% of the time. Flat, draft and steep ramps all give the same KO rate. | **Unanswered combo.** This one change cuts overtime by about 40%. |
| **Trade-backs (the real problem)** | After a shatter the scorer has about 1 piece on the board and the victim about 2. The victim scores straight back 29–37% of the time, which keeps HP level. | Unanswered combo plus red alert. "Knockback" (also removing the victim's oldest piece) gave fewer KOs and didn't help. **Don't use it.** |
| **Crit** | **Works as a rare highlight.** With W=7 and the ghost rule it is possible but rare: 0.92% of scoring moves, and in about 1–2% of skilled games. It only happens after an opponent's shatter breaks the X/O alternation. | Keep it as an occasional event. Making it common needs W=8 or more, which costs skill and adds draws. W=9 removes skill as a factor, and 4x4 with 3-in-a-row favours X by up to 33%. |
| **Red alert, double damage** | **Works.** It breaks most of the remaining ties and enables comebacks: winning from two lines down goes from 0% to 8–13%. | Use ×2. At ×1.5 comebacks drop to about 1%. |
| **Steeper ramp (25→50→75)** | Slightly more KOs (7.0/5.6/8.3 per 10) and similar fairness | Optional, for a more swingy game |
| **W=5 with ghost rule** | **Broken:** nobody can ever complete a line | Never ship it |

## 4. Expected stats (recommended ruleset)

Results come from roughly 1.2M simulated bot games and were checked by an independent rewrite of the simulator. Sampling noise is about ±1.6 points on a win rate and about ±3 points on the X-vs-O edge.

| Pairing | KO rate | Overtime |
|---|---|---|
| casual v casual | 36% | 24% |
| club v club | 21% | 38% |
| strong v strong | 53% | 19% |

The first draft had 53%, 70% and 46% overtime for the same pairings.

- **Skill:** club beats casual 62%, strong beats club 66%, strong beats casual 73%. The first draft gave .66, .75 and .82–.85, so the new rules trade a little skill weight for more KOs and comebacks.
- **Fairness:** the X-minus-O edge was −3.2% for strong vs strong, which is within noise. Under the first draft the edge was ±3% or less for skilled play but +9–12% for novices, because a KO by X ends the game before O's last turn. Letting O reply after a KO overcorrects (O ahead by 4–17%), so don't do it.
- **Openings:** centre, corner and edge all score about 0.49–0.53 for X.
- **Stalling:** doesn't help, because equal turns remove the benefit.
- **Pace:** under the first draft, the first line came at a median of 36–40 s for skilled players, with about 2.2–2.9 lines per minute and 41–47 moves per game. Sudden death lasted 5–10 moves (15–30 s). Pace figures were not reported separately for the recommended ruleset.

## 5. UI and feel notes

These are design suggestions. The simulation did not test them.

- **Health bars:** 125 HP with 25-point steps is 5 base hits. Show segment ticks every 25 so players can count "lines left". Under red alert, preview the doubled damage on the bar, for example a flashing ghost-segment of 50, 70 or 100.
- **Shatter impact frame:** a short hit-stop when a line shatters, with pieces flying out of both the board and the queue display. A crit needs a clearly bigger, separate effect because it is rare (about 1–2% of skilled games).
- **Combo meter:** with the unanswered combo the meter now matters. Show it as "armed" across your blank turns, and break it visibly when the opponent scores so the reset is obvious.
- **Red alert (last 30 s):** change the colours, play an alarm, and show a ×2 badge on the damage numbers. This is when comebacks happen, so make it feel like a different phase.
- **Overtime:** a clear "next line wins" banner, and show who moves first.
- **Trade-backs:** after a shatter, highlight that the victim has more pieces on the board. Players should understand why a counter-hit is likely.

## 6. Risks

- **Turtling (pure defence)** is the main abuse risk. It loses (44% vs casual, 35% vs club, 12% vs strong), but it drags games into slow overtimes. Turtle vs turtle gives 0.4 lines per minute and 89% overtime. Red alert probably reduces this, but that was not measured for ruleset C.
- **Overtime is still 19–38%** under the recommended rules. No setting in the sweep got it below about 40% without a rule change, and the rule changes bring it down to about 20%, not to zero.
- **X wins 57–59% of overtimes.** Adopt the "conceder starts overtime" option if this matters.
- **Novice games favour X by about 10%** because of KOs. This is acceptable for casual play but visible.
- **The recommendation trades skill for drama.** Strong vs casual goes from 82% to 73%. If ranked play needs a stronger skill signal, use ruleset B.

## 7. Still unverified

- **Bots only:** none of the results come from human players. The bots ignore the clock and the red-alert window, so real players will play differently near time-out, for example saving threats for double damage.
- **Pace under ruleset C:** game length, lines per minute and time to the first line were only reported for the first draft.
- **Turtle and stall bots under ruleset C:** not re-run.
- **Assumed move times:** uniform 1.5–4.5 s per move, with 2% timing out at 5 s. Real players' timing has not been measured.
- **Overtime cap:** the 40-move limit is a simulator stopping rule. The game itself has no defined end for an endless overtime.
- **Mislabelled sweep table:** in the 216-config sweep, the rows labelled "W7" and "W9" mix ghost-on and ghost-off games. W=7 with the ghost rule alone gives 0.88 KOs per 10 games and 66% overtime, not 2.1 and 54%. The other rows of that table mix settings the same way. The ruleset A, B and C tables and the recommendation don't use those rows.
- **Crit damage in red alert:** assumed to double along with line damage. Confirm this is intended.
- **UI and feel notes:** none were tested.

Simulator: /private/tmp/claude-501/-Users-ali-RiderProjects-TicTacToeUltimate/ec628b3a-bc0d-4f1a-be21-b5be550ace9d/scratchpad/arena/sim/ (24 tests pass; results are in the `results/` folder there)
Independent check: /private/tmp/claude-501/-Users-ali-RiderProjects-TicTacToeUltimate/ec628b3a-bc0d-4f1a-be21-b5be550ace9d/scratchpad/arena/check/