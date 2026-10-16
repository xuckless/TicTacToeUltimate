# AGENTS.md — Tic Tac Toe Ultimate

Living record of product decisions, implementation notes, and open questions.
Agents working in this repo: read this first, and append decisions here (with date) rather than re-asking.

## Vision
Competitive "never-ending" tic-tac-toe (pieces disappear, so games always end in a win). PvP first; PvE (bots + server-side algorithm, written in Go by the owner) later.
Funded by ads. Aesthetic: chess.com mobile app layout + Duolingo-style animated bits. **Main theme color: purple.**
Cosmetic packs: deferred.

## Decisions (2026-10-15)

### Stack
- **Clients:** keep Blazor + .NET MAUI Hybrid with the shared Razor class library (`TicTacToeUltimate.Shared`). **Web is the first target**; MAUI comes later.
- **Game server:** keep ASP.NET Core + SignalR as the authoritative match server. Go bots and services connect later as ordinary clients or through a bot API.
- **BaaS:** Supabase (Auth + Postgres) for identity, profiles, ratings, friends and chat history. The .NET server validates Supabase JWTs and runs live matches.
- **Local dev:** a DevAuth stub (pick a fake user) plus in-memory stores. Docker, Supabase and Redis are **not** required to play locally. Supabase migrations and SQL are written but stay unwired until keys exist. "Code only, test without login."

### Accounts
- "No login" from the original brief is **discarded**. There are two modes:
  - **Guest:** device-stored ID and a random name (e.g. `Guest#4821`). Casual PvP only. No ranked play, leaderboards or chat. Progress (streak, history) carries over when the guest upgrades to an account.
  - **Account:** the player picks a username and gets ranked play, leaderboards, friends, chat and challenges.
- Providers: email+password, email magic link/OTP, Google, Apple. **Build stubs and fixtures now; the owner adds API keys later.**

### Modes & clocks
- **Ranked (sudden death):**
  - 15s per move.
  - At 1:00 of total elapsed game time the limit drops to 10s.
  - After that it shrinks by 1s every 12s (9s at 1:12, 8s at 1:24, …), with a floor of 2s per move from 2:36 on, indefinitely.
  - High-tension "red alert" animations and sounds that escalate with the clock. Heavy use of **impact frames**.
  - Timeout = **loss**.
- **Casual:** the ranked schedule scaled ×2 or ×4. For ×2: 30s, dropping to 20s at 2:00, with a 4s floor. Unrated. Timeout = **auto-play a random legal move**.
- **Unlimited:** no clock.
- **Daily:** correspondence/async.
- **Friend challenge via link/room code.**
- First web build includes all of the above.
- Disconnect: the player's clock keeps running and reconnecting resumes the game. If nobody has moved yet, the game is aborted with no rating change.

### Rating, tiers, retention
- **Glicko-2**, with a separate rating per time control.
- **Visible rating-based tiers:** Bronze/Silver/Gold/Platinum/Diamond/Master with divisions. Promotion and demotion are animated.
- **Streak:** play 1 game per day. A rewarded ad earns a streak freeze (proposed; confirm).
- **Daily quests:** win 1, 3 and 5 games per day.

### Rules / fairness
- Legacy rule (`w1`, the old engine bug where the vanishing piece could still count; 3x3, global FIFO window of 7) was **solved**: X wins by force by opening in the center (8 plies); every other opening loses for X; there are no draws. Verified by two independent solvers (`tools/study/verify_solve.py`). Superseded by the FINAL rule below.
- The owner likes the **swap/pie rule** and asked to explore bigger grids and alternative algorithms.
- **LAUNCH RULESET (owner decision, 2026-10-15, after playtesting; REPLACES the `w0` ghost rule below): "3 pieces each, your own oldest vanishes first".** Ids: Ranked/Daily `n3k3-cap3-v0-w1-pie`; Casual, Unlimited, Challenge, Offline "Classic" `n3k3-cap3-v0-w1-none` (`RuleSet.Default` / `RankedDefault`).
  - Each player holds at most 3 pieces (PerPlayerCap W=3). Placing a 4th removes the mover's OWN oldest piece first; the mover may not place on that just-vacated cell this turn (`CellJustVacated`); then ANY 3-in-a-row of the mover's visible pieces wins immediately. No ghost exclusion.
  - Why: in playtest a player completed a visible 3-in-a-row and did not win because one piece was their own ghost, with no warning. The `w0` interpretation below was a misunderstanding of the owner's rule.
  - Solved: reachable 128,170, non-terminal 116,074; X wins in 13 from the empty board; edge openings X wins in 12; corners and centre draw; **pie: draw** (balanced).
  - UI: the mover's piece that vanishes when they place is faded + dashed with the caption "Your faded X vanishes when you place — you can't play there this turn"; clicking it explains instead of failing silently. The waiting side's oldest gets a subtler ⏱ marker. Threat hints (`BoardHints.Threats`) use `Rules.ImmediateWins` on each position after the mover's placements and list only playable cells; hidden when the mover can win now.
  - The old ghost rule stays available (`RuleSet.LegacyFifo7Ghost`, Offline "Legacy 7-piece" preset) and is still the **Arena** board (Arena rules unchanged; separately tuned, owner to be asked).
- **Superseded (was "FINAL", 2026-10-15): "the piece about to vanish can't count toward a win" (`w0`).**
  - The mover's oldest piece is next to vanish (when the window is full, W=7). It is excluded from the mover's win check, so effectively each player plays with 3 live pieces plus 1 "ghost".
  - Solved: X wins in 13 from the empty board. Edge openings win for X in 12; centre and corner openings are draws (perfect play cycles forever, so the clock decides). 11.6% of positions are draws.
  - **With pie, perfect play is a draw**, so ranked is fair and decided by skill under the clock.
  - Ruleset ids (superseded): Ranked/Daily `n3k3-fifo7-v1-w0-pie`; Casual, Unlimited, Challenge `n3k3-fifo7-v1-w0-none`.
  - Under that rule the UI showed the mover's ghost piece faded and never drew a win line through it.
- **4x4 (study, not yet chosen; research solvers only, no product code).** Re-solved 2026-10-15 under the binding rule. Full output: `tools/study/results-4x4.txt` (re-run with `tools/study/4x4/run_matrix.sh`; regression check `tools/study/4x4/regress.sh`). Rule flag in `psolve_sym.c` / `vi.c`: `w1` legacy, `w0` = engine rule (ARCHITECTURE §4.2 step 7), `g` = stricter "mover's own oldest piece is a ghost".
  - Important: under the engine's `w0` the excluded piece is `NextToVanish` after the placement. With an **even** window (W=8) that piece is always the opponent's, so **`w0` changes nothing at W=8**. With an odd window (W=9) it is the mover's oldest piece, as on 3x3 W=7.
  - 3-in-a-row: X wins in 5 plies under every rule and window. Too easy.
  - 4-in-a-row: a draw from the start under `w0` at both W=8 and W=9 (85-96% of positions drawn). Too drawish. (Legacy W=9 with the vacated cell playable was an O win in 44, but that rested on the vanishing-piece win.)
  - 4-in-a-row OR a 2x2 square (the "exciting" candidate):
    - **W=9, vacated cell playable, `w0`** (same FIFO/vacated/w0 flags as the 3x3 launch rule, but an odd window of 9): X wins by force in 29 plies. Every opening wins for X: centre 28, edge 36, corner 58. Max forced distance 87; 0.44% of positions drawn. **Pie: the chooser (O) wins by force**, unlike 3x3 where pie is a draw.
    - W=8, vacated cell blocked: identical values (X in 29; openings 28/36/58), because `w0` is a no-op at even W. This is the old "36-58 plies" result, now confirmed under the binding rule.
    - W=8 with the vacated cell playable, and W=9 with it blocked: draws from the start (91% / 52% of positions drawn).
    - Under the stricter `g` reading at W=8 each player has only 3 live pieces, so no 4-cell shape can ever be completed: 100% draws.
  - **Verdict vs 3x3 (`w0`: X wins in 13, 11.6% draws, pie = draw):** 4-row-or-2x2 at W=9 is far deeper (X wins in 29, openings need 28-58 plies of perfect play, beyond human calculation), so it should feel more open and exciting. But it is less fair in theory: there is no drawn opening, so pie is a forced win for the chooser, while on 3x3 pie is a draw. The engine also cannot express a 2x2-square win yet (`LineTable.For(N,K)` is lines only), so a "Big Board" mode needs a contract request for a square win shape first. Candidate for a later "Big Board" mode.
  - **W=7 (the earlier "best candidate") is dead under the binding rule.** Legacy `w1`, 4-row-or-2x2, vacated playable: X wins in 31; centre openings win, edges lose, corners draw; 0.58% draws; forced wins up to 37 plies; balanced under pie (reproduced). Under `w0` the mover holds 4 pieces after a full-window placement and one is the ghost, so only 3 live pieces remain and no 4-cell shape can ever be completed: **100% draws**.
- The rule study's independent recommendation for Ranked was the "fading piece" rule (= the owner's rule) + pie, matching the decision above.

### Arena (health) mode — experimental idea (owner, 2026-10-15)
- Completing a line does not end the game. The line's pieces **shatter** (removed from the board and the FIFO) and deal damage.
- **Health bars:** 100 HP each, a line deals 25. 0 HP = KO; otherwise the higher HP at time-out wins.
- **Bonuses chosen:**
  - **Double line = critical hit.** Caveat: under the ghost rule on 3x3 W=7 a player has at most 3 live pieces, so a double line may be impossible. The simulation will check.
  - **Combo streak:** scoring on consecutive own turns ramps the damage.
  - **Sudden-death overtime:** tied HP at time-out means the next line wins.
- **Clock:** a 2:00 round, a 5s per-move limit, and the last 30s in red alert. Equal turn counts for both players.
- Status: a tuning simulation is running (bots, parameter sweep, independent re-check). Not in the current build scope. It will be added as a ruleset/mode extension once tuned.
- **Simulation results (2026-10-15)** — about 1.2M bot games, independently re-checked. Code: `scratchpad/arena/{sim,check}`, to be copied to `tools/arena-sim` at integration.
  - **The first-draft rules fail:**
    - 46–71% of skilled games end tied and go to overtime.
    - The combo almost never triggers (0.06–1.2%), because the streak resets on any blank turn.
    - The real cause of ties is trade-backs: after a shatter the victim has more pieces on the board and scores straight back 29–37% of the time.
  - **Crit is possible under the ghost rule but rare** (about 0.9% of scoring moves, 1–2% of skilled games). It is a highlight, not a strategy.
  - Never use W=5 with the ghost rule: nobody can ever complete a line.
  - **Recommended "ruleset C":**
    - 3x3, W=7, ghost rule on, 2:00 round, 5s per move, equal turns.
    - 125 HP. Line damage 25→35→50.
    - **Unanswered combo:** the streak survives your blank turns and resets only when the opponent scores.
    - Crit = lines × base + 15.
    - **Red alert = ×2 damage in the last 30s.**
    - KO is checked before time-out. Overtime: the next line wins.
  - **Results with ruleset C:**
    - KO rate 21–53% and overtime 19–38%, depending on the skill pairing.
    - The stronger player wins 62–73%.
    - The X/O edge is within noise for skilled play, but about +10% for X among novices.
    - Comebacks from 2 lines down: 8–13%.
  - **Ruleset B** (draft + unanswered combo only) is more skill-heavy (strong players win 65–80%) but has more overtime.
  - Optional: the player who conceded the last line starts overtime, which removes X's 57–59% overtime edge.
  - Risks: turtling drags games into overtime. All numbers are bot-only and need human playtests.
  - **DECIDED (owner, 2026-10-15):**
    - **Ruleset C** for Casual Arena and the Arena **Ranked Ladder**.
    - **Ruleset B** is saved for a separate future **"Competitive mode"**: a skill-heavy queue whose exact format is still to be defined (tournaments? top tiers?).
    - **The player who conceded the last line starts overtime.**
    - **Build Arena right after the current PvP rebuild merges and passes its tests.**
  - **IMPLEMENTED (2026-10-15):**
    - Engine: `TicTacToeUltimate.Engine.Arena` (`ArenaRules`, `ArenaEngine`, `ArenaState`) is a pure scoring layer; classic `Rules` is untouched. Named rulesets `arena-c` (Casual Arena + Arena Ranked Ladder) and `arena-b` (stored for Competitive, not in the UI). Ghost rule on. Round clock = sum of credited think times; red alert, KO-before-time-out, equal turns and conceder-starts-overtime are all in the engine.
    - Vectors: `spec/arena/vectors.json` (RulesTool `gen`), cross-checked against `tools/arena-sim` (222 cases, 0 mismatches). Tests: `ArenaTests` mirror `test_arena.py`.
    - Server: new time controls `ArenaCasual` (unrated, guests ok, timeout = auto-move) and `ArenaRanked` (own visible Glicko pool `ArenaRanked`, timeout = loss), both 5 s per move, never bots, no analysis. New end reasons `Knockout`, `Decision`, `Overtime`. Config `Ttt:Modes:Arena*:Arena = "arena-c"`. Migration `20261015000006_arena.sql` adds the pools.
    - Contracts (additive): `ArenaRulesDto`, `ArenaStateDto`, `ArenaHitDto`; `MatchSnapshotDto.ArenaRules/Arena`, `ActionApplied.Arena`, `ModeConfigDto.Arena`.
    - UI: health bars with 25-HP ticks and a red-alert ghost segment, shatter burst + hit-stop, crit impact, combo meter (armed / broken), red-alert ×2 phase, overtime "next line wins" banner with the first mover; Arena card on Home, two Arena modes on Play, Arena leaderboard tab.

### Ranked fairness / anti-abuse
- **Rematch only in non-competitive matches.** Ranked rematches would allow win trading.
- **Ping fairness:**
  - The server owns the clock.
  - Each move is credited back half the player's measured RTT, capped at about 300ms per move, with a per-game compensation budget.
  - Matchmaking should prefer opponents in the same region where possible.

### Social
- **In-game:** canned phrases plus emotes (Clash Royale style). No free text.
- **Outside games:** free-text chat, **only between mutual friends**.
- **Friends:** request/accept, block, presence (online / in game / offline), direct challenge, spectate friends' live games.

### Profiles & leaderboards
- **Usernames:** 3–16 chars, `[a-z0-9_]`, unique (case-insensitive), with a profanity/reserved-word filter and 1 free rename.
- **Profile:** preset avatar (cosmetics later), country flag, tier, ratings and history.
- **Leaderboards:**
  - Boards: global per time control (requires at least N rated games), friends, country.
  - **Seasonal resets apply only to the Master tier.** Everyone else keeps their rating indefinitely.

### Post-game
- Game history + move-by-move replay.
- **Perfect-play engine analysis:** the solved tablebase marks each move as winning or losing ("Blunder!"). It is behind a rewarded ad.
- Spectate live games.

### Matchmaking
- Rating window starts at ±100 and widens by 50 every 3s.
- Guests are matched with guests, with a fallback after about 10s.
- If no human is found, fall back to a bot (once the Go bots exist).

### Daily format
- 24h per move, up to 10 concurrent games.
- Its own Glicko rating.
- Notification when it's your turn.
- Timeout = loss.

### Platforms & monetization
- **Feature parity across web, iOS, Android and Mac.** Only ads and IAP differ. Apple sign-in on iOS and web. Push notifications on mobile only. **Windows and Tizen targets are dropped.**
- **Ads:** mobile-game style, everywhere except during a live match.
  - Banners on menus.
  - Interstitial every N games.
  - Rewarded ads that players watch in exchange for perks: streak freeze, extra quest, post-game analysis, and more.

### UI / UX
- **Navigation:**
  - Bottom tabs: **Home / Play / Social / Profile**. On desktop web these become a left sidebar.
  - Home: streak, quests, Play hero button, friends online, banner ad.
- **Match screen (chess.com style):**
  - Opponent card at the top (avatar, name, tier, clock), your card at the bottom, board in the center.
  - The piece about to vanish is drawn faded.
  - Emote, resign and menu buttons sit by your card.
- **Theme:** dark-first with a light option. Purple primary.
- **Feedback:**
  - Full SFX: place, vanish, win, and a tension heartbeat that speeds up in sudden death.
  - Impact-frame stingers, haptics on mobile, music toggle. Each is toggled separately.
- **Design:** Claude Design produces the design system and key screens; their tokens get ported into the Blazor CSS.

### Repo cleanup (approved)
- Untrack bin/obj.
- Untrack `appsettings.Auth.json` / `appsettings.Stripe.json` and add `.sample` files. **The owner should rotate those keys; they remain in git history.**
- Delete template and dead code: Counter, Weather, DbTestPage, SignalRTestPage, AuthTest, TestHub, Domain.Shared, Stripe.
- Drop the Windows and Tizen MAUI targets.

### Rebuild spec (2026-10-15)
From `docs/ARCHITECTURE.md` §1 (binding; [OPEN] items have working defaults).

- **D1 Topology.** The authoritative game server stays a **separate ASP.NET Core host**, `TicTacToeUltimate.Server`, which replaces `BackendWebAPI`. `TicTacToeUltimate.Web` only serves the Blazor WASM app. Web (browser) and MAUI both connect to the server URL from config. CORS is enabled for the web origins. A web deploy never restarts live matches.
- **D2 Web render mode.** Global `InteractiveWebAssembly` with `prerender: false`. There is no Interactive Server, so there is no circuit double hop to distort RTT.
- **D3 One engine everywhere.** `TicTacToeUltimate.Engine` is pure and deterministic, with no package dependencies. It is used by the server (authority), the client (derived UI only: faded piece, legal hints, threat hints, replay), the RulesTool and the tests. Go conforms through the JSON vectors.
- **D4 Ruleset.** **Superseded 2026-10-15 (playtest):** default `n3k3-cap3-v0-w1-none`, Ranked/Daily `n3k3-cap3-v0-w1-pie` (3 pieces each, own oldest vanishes first, just-vacated cell not playable this turn, any visible line wins; see "Rules / fairness"). Former text: the default was 3x3, K=3, global FIFO W=7, vacated cell playable, **the piece about to vanish cannot count toward a win** (`VanishingPieceCanWin=false`, id segment `w0`). The old BackendWebAPI engine let it count; that was a bug. **Ranked and Daily** add the pie rule (`n3k3-fifo7-v1-w0-pie`). Casual, Unlimited and Challenge use the default with no opening rule. Everything is configurable per mode (§6.10). With pie, perfect play is a draw (endless cycle), so ranked games are decided by skill under the clock. 4x4 variants remain under study; 3x3 is final for launch.
- **D5 Pie mechanics.** After X's first placement, O (the chooser) takes a separate turn with a clock to choose Swap or Keep. With Swap, the board colours are unchanged and the colour O is still to move, but **the seats exchange colours**: the first player now owns O and moves next. The engine tracks `Swapped`; the server maps seat↔colour (`SeatColors`). The tablebase does not need to know about pie.
- **D6 Move allowance is fixed when the turn starts:** `allowance = MoveClock.Allowance(tc, gameElapsedAtTurnStart)`. Text like "window 8s → 7s in 5s" describes what *future* turns will get.
- **D7 Game elapsed** is the **sum of the credited think times (`ThinkMs`) of all completed turns**. A swap decision is a turn. This makes the schedule replayable from the action log and independent of server queueing.
- **D8 Lag compensation follows AGENTS literally.** **Every** move is credited `credit = min(halfRtt, LagCreditCapMs=300, budgetLeft)`. `halfRtt` is half the minimum of that player's last 5 server-measured RTT samples, frozen at turn start. The budget is debited by `credit` on every move. The default budget is 4000 ms per player per game. The move is accepted iff `rawThink <= allowance + credit`, and `ThinkMs = max(0, rawThink − credit)`. The opponent's displayed countdown runs against the nominal deadline (`turnStart + allowance`). At 0 it shows `0:00` with a "…" until the server's result arrives, because the credit can extend acceptance by up to 300 ms.
- **D9 Abort.** This is AGENTS literal: if the game ends (timeout, resign, leave) **before the first action (ply 0)**, it is `Aborted`: no rating change, no streak or quest credit. After ply 0, normal rules apply, including when the chooser times out on the pie decision (Ranked: loss). **[OPEN] whether to also abort when O misses its first action.**
- **D10 Timeouts.** Ranked: loss. Daily: loss. Casual ×2/×4: the server auto-plays a deterministic random legal action, `SplitMix64(seed ^ (ulong)ply)`; in the pie phase the auto action is Keep. The action is recorded with `Auto=true` and the game continues. There is no AFK forfeit. Unlimited: no clock. If the player to move stays disconnected for `Unlimited:DisconnectForfeitMinutes` (default 10), they lose (`Abandoned`).
- **D11 Ratings.** There is one Glicko-2 rating per time control: pools `Ranked, CasualX2, CasualX4, Unlimited, Daily`. Only **Ranked and Daily are visible** (tiers, leaderboards, deltas shown). Casual and Unlimited pools are **hidden** and drive matchmaking only. Challenges never change ratings. Draws score 0.5.
- **D12 Tiers.** **[OPEN] bands.** The default bands match the mockups ("Gold II 1552, 48 to Gold I"). Bronze < 1100, Silver 1100–1399, Gold 1400–1699, Platinum 1700–1999, Diamond 2000–2299, Master ≥ 2300. Divisions are III/II/I, each 100 wide (for example Gold III 1400–1499, Gold II 1500–1599, Gold I 1600–1699; Bronze III is everything below 900). Provisional (first 10 rated games): the tier is shown with a "Placement" badge.
- **D13 Guests.** Guests may play Casual ×2/×4 and Unlimited from the queue, and may **accept** a challenge link. Guests may **not** play Ranked or Daily, create challenges, use friends, chat or spectate, or view leaderboards. Guest progress keeps the same user id on upgrade.
- **D14 Rematch.** Rematch is allowed only when the match is not visibly rated (Casual, Unlimited, Challenge). Both players offer within 60 s; the new match swaps seats.
- **D15 Threat hints** ("BLOCK IT!") are a client-side setting. They are **forced off in Ranked and Daily** because they are tactical assistance in a solved game. **[OPEN]**
- **D16 Spectating.** Friends of either player may spectate (accounts only). There is no delay. **[OPEN] ranked spectator delay.**
- **D17 Ads.** The client-side pure `AdPolicy`: an interstitial every 3 completed games, never right after the second loss in a row, never on match routes, never while queued. Banners appear only in the app shell. Rewarded kinds: StreakFreeze, ExtraQuest, Analysis. Stub providers only (web and MAUI). **`Plugin.AdMob` is removed**; real AdMob returns later behind `IAdProvider`.
- **D18 Persistence.** In-memory repositories by default. Postgres (Npgsql + Dapper, no EF) is selected only by `Ttt:Persistence:Provider=Postgres`. The schema lives in `/supabase/migrations`. RLS is enabled with no anon or authenticated policies, and the server uses the service role.
- **D19 Identity.** `UserId` = JWT `sub` (Guid) for every issuer. Supabase users get `players.id = auth.users.id` with **no FK** to `auth.users`, so dev fixtures, guests and bots need no auth rows.
- **D20 Draws exist** only where a ruleset produces them (no legal move = Draw). There is no move cap.
- **D21 The pure clock schedule is in the engine** (`MoveClock`) so server, client and Go share it through `spec/clock/schedule.json`.
- **D22 Notes.** Each WP writes `docs/notes/WPn.md`. Only the lead edits `AGENTS.md`.

## Codebase audit (2026-10-15) — summary
- Tracked bin/obj (~4k files) and `appsettings.Auth.json` / `appsettings.Stripe.json` are in git despite `.gitignore`.
- No player identity or seats in `GameHub`; any client can move for either side or reset any game.
- Non-atomic Redis read-modify-write; `GetOrCreate` race; every client uses the hard-coded game id `"default"`.
- `PlayersController` is fully anonymous CRUD that exposes emails and allows mass-assigning Elo.
- Web.Client registers a hub that doesn't exist; Web registers two singleton `HubConnection`s, and the last one wins.
- Template pages (Counter/Weather), `TestHub`, `Domain.Shared` (empty), Stripe (unused) are dead code.
- MAUI build fails: the workload needs Xcode 26 and the machine has Xcode 27, and the android workload is missing. Use a web-only solution filter for now.
- Redis is not installed, the Docker daemon is not running, and Go is not installed.

## Design artifacts (Claude Design)
- **Design system:** https://claude.ai/artifact/AV8adr1SKa2syRre7U5se7
  - Tokens (dark-first + light), brand book, and components: Button, Board, PlayerClock, TierBadge, StreakQuest, BottomNav, ResultCard.
  - Fonts: Lilita One (display), Nunito (UI), Chakra Petch (clocks and ratings).
  - X = cyan `#38d6ff`, O = orange `#ff9f3a`, red reserved for sudden death.
- **Key screens canvas:** https://claude.ai/artifact/PwupG31Xjfc2h5hYnXo7aZ
  - Home, Match, Sudden death, Result, Social, Profile (390×844).
- Port `tokens.json` into `TicTacToeUltimate.Shared/wwwroot/app.css` as CSS variables. The design system is the source of truth.

### More decisions (2026-10-15)
- **Bot API:**
  - Plain WebSocket + a documented JSON protocol, with shared rule test vectors (C# and Go engines must agree).
  - Bots authenticate with an API key and show a BOT badge.
- **Age:** a 13+ ToS checkbox. No DOB is stored. Ads are non-personalized until consent.
- **Glicko-2:** start at 1200, RD 350, provisional for the first 10 games.
- **Ads:** interstitial every 3 games, never right after two losses in a row.
- **Streak freezes:** at most 2 banked. Earned by rewarded ads or by finishing all 3 daily quests.

### Toolchain (2026-10-15)
- **Target .NET 10 LTS (`net10.0`)** for all projects. .NET 9 goes out of support in November 2026.
- The owner installs the system SDK (`brew install --cask dotnet-sdk`) and the workloads (`sudo dotnet workload install maui android ios maccatalyst`). The system dotnet folder is root-owned, so agents can't install there.
- Installed: SDK 10.0.402 (runtime 10.0.13) with workloads ios/maccatalyst 27.0.10722 (these support Xcode 27, so no bypass is needed), android 36.1.118 and maui 10.0.110.
- Android: API 36 platform installed via `-t:InstallAndroidDependencies`. The default JDK 22 builds fine.
- Minimum OS: MacCatalyst 17.0 (the .NET 10 minimum), iOS 15.0, Android 24.
- All five targets build on net10.0 as of commit 5ae3810: backend, web, net10.0-ios, net10.0-android, net10.0-maccatalyst.
- Work happens on branch `rebuild/pvp-foundation`. The architecture spec is at `docs/ARCHITECTURE.md`.
- Mobile scope: build both web and mobile (iOS, Android, MacCatalyst) now.

## Open questions
- **Final ruleset** (grid size, window, removal rule) plus the pie rule. The variant study is pending. A rule change needs a re-solve plus a regenerated tablebase.
- Tier band boundaries (e.g. Bronze <1000, Silver <1200, Gold <1400, Platinum <1600, Diamond <1800, Master 1800+?).
- Friend chat moderation retention period.

### Branding & follow-ups (owner, 2026-10-15)
- **Logos** (Claude Design system, Logos group; source in `design/system/assets/Logos`, generator `tools/logo/gen.py`):
  - The mark is a 2×2 mini-board: solid cyan X and orange O on one diagonal, faded ghost pieces on the other, on a purple rounded square with a 3D ledge.
  - Files: `ttt-app-icon.svg` (app icon/splash), `favicon.svg` (≤48px), and wordmarks "TIC TAC TOE / ULTIMATE" (dark, light, stacked; Lilita One outlined).
  - TODO (integration): use them as the MAUI AppIcon/Splash, the web favicon and apple-touch icon, and the sidebar header mark.
- **LATER:** make the web UI less bulky on desktop. Denser sidebar, smaller type and padding at ≥900px, a less chunky Home hero and cards. Mobile keeps the chunky style. (The owner's screenshot of the 2026-10-15 web build showed it as too bulky.)
- **Offline PvP (pass & play)** on one device, no server: in progress on branch `rebuild/offline-pvp`.
- **Bug reported by the owner:** challenge-link creation doesn't work in the web build. Verify end to end after the integration fix.
- **Challenge links (fix, 2026-10-15):** the web "Invite by link" silently failed. A guest or username-less user got a HubException (`guest_not_allowed` / `username_required`) that the UI never caught.
  - **Decision:** guests may create and accept OPEN (untargeted) casual invite links, which are unrated and fit "guests = casual PvP". Friend-targeted and Daily challenges still require an account.
  - `InviteLinkButton` now shows specific error toasts.
### Offline Pass & Play (2026-10-15)
- Routes `/local` (setup) and `/local/play` (board, MatchLayout so no ads). `AuthGate` lets `local*` routes through with no session/server; its "Can't reach the server" splash links to Pass & Play.
- Logic: `TicTacToeUltimate.Client/Local/LocalGameController.cs` (pure engine + local `MoveClock`; host calls `Tick()` at 10 Hz). `LocalPlaySession` singleton shares it between pages.
- Rules: Classic `n3k3-cap3-v0-w1-none` (launch rule), or "Legacy 7-piece" `n3k3-fifo7-v1-w0-none`; optional pie (`…-pie`). Clocks: Unlimited (no timeout), Casual x2/x4 (timeout = random legal move, or keep sides in the pie decision), Sudden death = Ranked schedule (timeout = loss). Leaving the board pauses the clock.
- Rematch swaps who opens as X; session score kept in memory. No rating, streak, quest or ads.

### UI/UX overhaul decisions (owner playtest, 2026-10-15)
- **Threat hints ("BLOCK IT!")** are OFF by default. They come back only through a **Guided mode** toggle in Settings, which applies only in Casual, Unlimited, Challenge and Offline Pass & Play. **Never in Ranked, Daily or Arena**; the server enforces this.
- **Fonts:**
  - **Luckiest Guy** for big headline items: Play, VICTORY/DEFEAT, SUDDEN DEATH, KO, the "Tic Tac Toe Ultimate" logo text. The letters bounce up and down rather than sitting on a straight baseline.
  - **Fredoka** for all normal text and UI.
  - Chakra Petch stays for clocks.
- **Scaling:** for now, a **fixed phone-width game column** (about 420px, fixed px sizes). On desktop the column is centered, with a slim icon sidebar and the wallpaper filling the rest. The board snaps to fixed sizes by viewport height, and there is no vw/vh-driven sizing.
  - PLAN ITEM (later): a responsive desktop layout with the board beside panels (moves, chat, players), like chess.com.
- **Icons:** a custom chunky duotone SVG set (about 30 icons): thick rounded strokes, a purple fill plus a highlight, and X/O motifs.
- **Mascot + illustrated quest cards:** the mascot "Ghosty" (a purple blob in the shape of the vanishing piece) has expressions and appears on quests, streaks, empty states and results. Quests get illustrated badges, a reward chest, and chunky segmented progress bars that bounce when filled.
- **Ambience:**
  - Menus: slow falling dark-purple X/O wallpaper with depth layers.
  - Match: the wallpaper is dimmed and slowed.
  - **Sudden death:** a pulsing red glow around the whole viewport edge (web and mobile). The falling pieces turn red and speed up as the window shrinks.
  - Everything respects reduced motion.
- **Bug:** the VICTORY/DEFEAT text appears twice in the impact frame. Fix it.
- **Arena board switched to "3 pieces each" (owner, 2026-10-15).** Arena now uses `RuleSet.Default` (n3k3-cap3-v0-w1), so there is no ghost anywhere in the game.
  - Re-tuned in `tools/arena-sim` (new `cap=3` mode; `experiments.py cap3`, 1,500+ games per pairing per seat). Ruleset C numbers are unchanged.
  - Results vs the legacy board:
    - Overtime 10–29% (was 19–38%).
    - X/O within 49–51%.
    - The stronger player wins 71–88% (was 62–73%).
    - Comebacks from 2 lines down: 7–8%.
  - **Crits are now impossible** (you never hold 5 pieces). The code is kept dormant for a possible 4x4 Arena.
  - A shatter wipes all 3 of the scorer's pieces, which is a built-in comeback mechanic.
  - Vectors were regenerated and cross-checked against the Python sim: 152 positions match. 14 differ only because overtime's conceder-first order isn't modelled in the sim's untimed replay.

### Mode restructure (owner, 2026-10-15)
- **Ranked = Arena** (health bars, rated, Arena Glicko pool). This is the headline rating and tier shown on Home, Profile and Leaderboards.
- **Arena (unrated)**: casual Arena queue.
- **Casual** = normal Tic Tac Toe Ultimate (3 pieces each) on the **sudden-death clock** (15s → 10s at 1:00 → −1s/12s, floor 2s). Unrated; timeout = auto-move.
- **Unlimited** (no clock) stays. **Daily** (24h/move, rated, own pool) stays. **Friend challenge links** stay.
- **Casual x4 removed** (and x2) from the UI.
- **Old school**: plain tic-tac-toe, pieces never vanish, draws possible. Available in the online casual queue, Pass & Play and friend challenges.
- The classic Ultimate **Ranked** queue/rating is **stubbed in code but retired from the UI**.
- **Mascot renamed Ghosty → Bert (owner, 2026-10-15).** Bert has 15 moods: happy, laugh, jumpy, love, cool, think, wave, gg, sad, cry, angry, shocked, sleepy, wink, cheer. Each has its own animation, and reduced motion is respected.
  - **In-game emotes are now Bert emotes** (`EmoteCatalog` ids `bert-<mood>`, Kind `bert`, Text `bert:<mood>`). They replace the emoji emotes; the text phrases are unchanged.
  - **Emotes are thrown:** they fly in an arc from the sender's card across the board (online matches and Pass & Play/Arena, where each seat has an emote button).
- **Victory confetti:** brand-coloured strips plus tiny X/O pieces. It plays on your online win and on any local win, and is skipped under reduced motion.
- **UI polish round (2026-10-15):**
  - **Audit fixes:**
    - Cold deep-link `play?tc=…` now queues once the realtime connection is online. Previously it silently did nothing.
    - The hero "Start" button had no fill and looked disabled.
    - The Play-hero kicker overlapped the headline.
    - Option-grid (?) collisions are gone, and an odd last item now spans the row.
    - Switches and sliders are themed.
    - Desktop page top padding added.
  - **Flair:**
    - **X/O trinkets** perch on buttons and cards (`.perch-x` / `.perch-o` / `.perch-sm` / `.perch-mid`). They bob, and hop on hover or press.
    - **Bert flybys** in menus: random arc or edge-peek every 22–45s, and tapping him triggers a reaction.
    - **BertLoader** on every loading state, plus CSS pre-boot loaders on web and MAUI.
    - **LocalBurst** element-scoped impact frames: Play-hero press, mode-card press, quest completed ("QUEST COMPLETE!"), rating gain, invite created.
    - **GO!** impact frame at match start.
    - Staggered page-entrance animation.
  - All of it respects reduced motion.

### Bots (owner, 2026-10-15)
- **Go bot service** in this monorepo (`bots/`). A dynamic dispatch handler spawns one goroutine per requested session. Difficulty is sized to the opponent's Elo and adapts so **bots win ≈49%**.
- **C# bot request/dispatch service** in the game server. When no human match is available it asks the bot service for a bot, which becomes the opponent. **Real people always take priority over bots.** No online player is left waiting alone.
- Bots can play every queue: Casual, Old school, Unlimited, Arena (unrated), **Ranked (Arena ladder)**, and guests' queues.
- Difficulty: a rating-matched tablebase bot (perfect play from the solved table, with deliberate mistakes tuned to the player), plus humanlike think-time delays.
- Presentation: **human-like names with a small BOT tag.**
- **Monorepo:** Docker images plus GitHub Actions with path filters, so only the parts that changed get built.

#### Bots: as built
- `bots/` (Go 1.24, module `github.com/xuckless/tictactoeultimate/bots`): `botd` exposes HTTP `POST /v1/sessions` (bearer `TTT_DISPATCH_TOKEN`), `GET /v1/stats` and `/healthz`.
  - Each request starts one goroutine. It borrows a free **persona** (one bot API key with a human-like name, `config/personas.dev.json`), connects to `/bot/ws`, seeks that time control, plays one game and exits.
  - `internal/engine` is a Go port of the rules plus a retrograde tablebase. It is checked against every `spec/rules/vectors` file, including the tablebase counts and values.
  - `internal/brain`: base skill = P(perfect move), a logistic curve on opponent rating (0.30 + 0.62·σ((r−1400)/260)). A per-opponent adaptive staircase (−0.051 after a bot win, +0.049 after a loss) converges on a **49% bot win rate** in decided games. Tests check this against weak, medium and strong opponents.
  - Arena uses a board heuristic: complete a line, block, then centre and corners; mistakes are skill-gated. Think delay is 12–42% of the move allowance (≤1.9s in Arena).
- Server: `IBotDispatcher` (`HttpBotDispatcher` when `Ttt:Bots:Service:Url` is set).
  - The matchmaker pairs humans first.
  - After `DispatchAfterSeconds` (6) it requests one bot per waiting player.
  - After `BotFallbackSeconds` (12) it pairs the longest-waiting player with a seeking bot.
  - Every queue except Daily is covered, including Arena and Ranked (`Bots:Ranked=true`) and guests.
- Rated games vs a bot rate the bot as an equal: the human's rating with RD ≤150, since bots are rating-matched. Bots' own ratings are never stored.
- Persona keys live in `appsettings.Development.json` only. Production gets them from secrets (`Ttt__Bots__DevKeys__0__Name/Key`…), with the same list mounted into botd as `TTT_BOT_PERSONAS`.
- Run locally: `cd bots && go run ./cmd/botd` (port 7070; the dev server already points at it).

#### Monorepo build / deploy
- `.github/workflows/{server,bots,web}.yml` use path filters: only the parts that changed get tested and built.
  - Server and bot images are pushed to GHCR: `ghcr.io/<owner>/ttt-server`, `ttt-bots` (multi-arch).
- Web is a **static** Blazor WebAssembly build (`deploy/web/build.sh` → `deploy/web/out`). `TicTacToeUltimate.Web.Client/wwwroot/index.html` sets `window.tttStandalone`, so Program.cs mounts Routes itself.
  - **Vercel** builds it from `vercel.json`: it installs .NET via dotnet-install and adds SPA rewrites.
  - Env for the build: `TTT_BACKEND_URL`, `TTT_WEB_URL`, `TTT_ADSENSE_CLIENT`.

### Supabase, deployment, ads (owner, 2026-10-15)
- Supabase project **tictactoe-ultimate** (org tictactoe-ultimate, ref `mmoxnxiiqftnfqxqueob`, us-east-1). The DB password is in `.env` (gitignored).
  - Migrations are pushed. Dev keeps InMemory by default; set `Ttt__Persistence__Provider=Postgres` to run against Supabase (user-secrets hold the connection string, URL and publishable key).
- Auth: **email + password only** for now (confirmations on, anonymous off). Guests stay server-issued device tokens; `/api/auth/guest` is now mapped in every environment (it used to be Dev-only, which would have broken production guests).
  - **Guest history**: on email sign-in the client calls `/api/auth/claim-guest` with the old guest token (the signature is the proof). The server moves games and replays, streak, quests and hidden casual ratings, then deletes the guest row.
- Game history + replays are stored in `games` + `game_actions`. Verified end to end against Supabase: guest game → email sign-in → history → replay.
- Real-time spectating: the existing friend spectating over SignalR (match actors live in memory; there's no DB involvement).
- Deployment: web → **Vercel** (static); game server → Cloud Run with 1 instance or the workstation; bots → same, or Docker + Tailscale Funnel. Vercel can't host the C# server (no .NET runtime, no long-lived WebSockets). Full guide: `docs/DEPLOY.md`.
- Ads: web uses **AdSense**, with the Ad Placement API for interstitials and rewarded ads plus a display unit for the menu banner. It activates only when `Ads:AdSenseClient` is set and falls back to "no ad" after 5 s. The server's `Ttt:Ads:Provider=AdSense` accepts one-time `web:` nonces.

### UI round (owner, 2026-10-15)
- Pass & Play setup redesigned:
  - Players as an X-vs-O "versus" card.
  - Mode and rules as coloured illustrated tiles, with a check and a burst on select.
  - Pie rule as a themed switch.
  - A "Let's play!" hero button.
- Result sheets (Pass & Play, Pass & Play Arena) use the sports-style `Scoreboard`: avatars on either side with their piece, the reason pill over a big bold score, and a crown plus glow on the winner.
- Online result page:
  - The perfect-play card shows the final board greyed and rotated in the background (no more squashed mini board).
  - Quests show the Home `QuestCard`, which bursts quests completed by this win.
- Perched X/O trinkets removed everywhere (too distracting). Flybys now rotate Bert, X, O and an X+O duo (`PieceBuddy`: 2D X/O characters with faces, gloved hands, shoes; moods happy/wave/cheer), and come more often (every 14–30 s).

- **Ad blocker prompt (2026-10-15):** web only, when AdSense is configured. `tttAds.blocked()` runs a no-cors probe of the AdSense script plus a bait element. A dismissible Bert notice (`AdBlockNotice`, menus only, once per day) asks the player to allow ads. Rewarded ads that fail because of a blocker toast "Your ad blocker stopped the ad…" (`ErrorCodes.AdBlocked`). Note: the dev Mac blocks googlesyndication at DNS level (local resolver 127.31.17.22), so ads never load there.

### Production + mobile release (owner, 2026-10-15)
- **Live:** https://tictactoeultimate.alidirect.dev (Cloudflare Worker: static site plus a same-origin API proxy to the VM).
  - GCE e2-micro `ttt-server` (project `ttt-ultimate-b7097a`, us-east1) runs Caddy + server + bots in Docker. It is always on (free tier); containers use `restart: always`.
  - Origin locked by an `X-Origin-Auth` secret.
  - The apex `alidirect.dev` Worker serves `ads.txt` and redirects to the game.
- Server cold start is about 25 s (tablebases). The client shows a "Waking up the game server" buffer, retrying 5xx and network errors for up to 60 s.
- SEO: meta, Open Graph, Twitter and JSON-LD on the index; crawlable summary; static `/how-to-play/ /about/ /privacy/ /terms/`; sitemap, robots, favicon set, apple-touch icon, manifest, OG image.
- AdSense publisher `ca-pub-7292645320711380`. Banner hidden until a slot id exists. With no fill (H5 not approved) the reward is granted anyway; blocked → prompt.
- **Ad pacing (owner: "same frequency as Duolingo")**, web and mobile:
  - an interstitial every 2nd completed game, at least 120 s apart;
  - none in a session's first 2 games, none right after a 2nd loss in a row;
  - never in a match or a queue;
  - rewarded ads are opt-in for perks.
  - Config: `Ttt:Ads:{InterstitialEveryGames,MinSecondsBetween,FirstAfterGames}`.
- **Mobile ads:** AdMob via Plugin.AdMob 10.0.90 (interstitial + rewarded, UMP consent, iOS ATT before the first ad, no banners). Google test IDs until real IDs are set (see DEPLOYMENT_GUIDE.md). Receipts are `admob:` nonces (SSV later). Mac Catalyst: no ads, rewards granted.
- Mobile Release builds target production. Android upload key is in `~/.ttt-signing` (signed AAB builds). iOS signs through `TTT_IOS_CODESIGN_KEY` / `TTT_IOS_PROVISIONING_PROFILE`.
- Deep links are now actually handled (`DeepLinkHandler`): `tttu://c/CODE` and `https://…/c/CODE` open the challenge.
- Git history rewritten (2026-10-15) to purge `BackendWebAPI/appsettings.{Auth,Stripe}.json` and all bin/obj/.idea (310 MB → 2.4 MB). Merged work branches deleted. `main` = `rebuild/pvp-foundation`. **Not pushed:** needs `git push --force origin main`. The old keys must still be rotated.
- `DEPLOYMENT_GUIDE.md` (gitignored) holds the operational runbook and the secret locations.

### Restart safety (owner bug report, 2026-10-15)
- **Graceful deploys:** `MatchShutdownService.StoppingAsync` runs before Kestrel stops. It sets `ServerDrain`, which makes queue entry, rematch and challenge accept answer `server_updating`; the matchmaker keeps the queue but pairs nobody. It then waits up to `Ttt:Shutdown:DrainSeconds` (Production 600; compose `stop_grace_period: 11m`) for live non-Daily games to end, and finally closes SignalR and bot sockets so the process exits.
- Clients: the queue re-enters automatically after a reconnect, and the queue sheet explains "server updating".
- If a game is lost anyway (crash), the match page shows **"Game interrupted"** (Play again / Home) instead of a frozen board.
- Bots were OOM-killed at 64 MB: the limit is now 160 MB with `GOMEMLIMIT=110MiB`.
- Invite sheet off-screen: the page-entrance animation filled `transform` (`both`), which made page children containing blocks for `position:fixed`. It now uses `backwards`.
