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
- **FINAL launch ruleset (owner, 2026-10-15): "the piece about to vanish can't count toward a win". This was always the original rule; the old engine letting it count was a bug.**
  - The mover's oldest piece is next to vanish (when the window is full, W=7). It is excluded from the mover's win check, so effectively each player plays with 3 live pieces plus 1 "ghost".
  - Solved: X wins in 13 from the empty board. Edge openings win for X in 12; centre and corner openings are draws (perfect play cycles forever, so the clock decides). 11.6% of positions are draws.
  - **With pie, perfect play is a draw**, so ranked is fair and decided by skill under the clock.
  - Ruleset ids:
    - Ranked/Daily: `n3k3-fifo7-v1-w0-pie`.
    - Casual, Unlimited, Challenge: `n3k3-fifo7-v1-w0-none`.
  - The UI must show the mover's ghost piece faded and never draw a win line through it.
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
- **D4 Ruleset.** The default is the OWNER'S ORIGINAL RULE (decided 2026-10-15): 3x3, K=3, global FIFO W=7, vacated cell playable, **the piece about to vanish cannot count toward a win** (`VanishingPieceCanWin=false`, id segment `w0`). The old BackendWebAPI engine let it count; that was a bug. **Ranked and Daily** add the pie rule (`n3k3-fifo7-v1-w0-pie`). Casual, Unlimited and Challenge use the default with no opening rule. Everything is configurable per mode (§6.10). With pie, perfect play is a draw (endless cycle), so ranked games are decided by skill under the clock. 4x4 variants remain under study; 3x3 is final for launch.
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
