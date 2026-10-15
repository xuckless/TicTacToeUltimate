# TicTacToeUltimate PvP rebuild: final implementation spec

Status: **FINAL for implementation** (2026-10-15). Branch `rebuild/pvp-foundation`.
Precedence: `AGENTS.md` > this document > anything else. If this document contradicts `AGENTS.md`, `AGENTS.md` wins. Report the conflict in your WP notes file (§13.3); do not silently pick.

This spec is written for **7 implementers who never talk to each other**: one lead (WP0) runs first, then six work packages (WP1..WP6) run in parallel. Every cross-package seam is a C# type printed verbatim here, or a JSON/HTTP/WebSocket shape printed verbatim here. If a seam you need is missing, follow §13.4 (contract requests). Do not invent a seam inside another package's folder.

Synthesis note: this spec starts from the highest-scoring proposal (single-reader actor per match, outbox publisher, idempotent moves, generated rule vectors). It fixes the defects the judges found and adds the strongest ideas from the other two proposals: the pure clock schedule in the engine, a non-UI client core, partial hub files per owner, null implementations so the server boots on day 1, and repository contract tests.

---

## 0. Ground truth (verified on this machine, 2026-10-15)

### 0.1 Toolchain. Nothing to install.
- .NET SDK **10.0.402** (runtime 10.0.13). Workloads: ios/maccatalyst **27.0.10722** (supports the installed Xcode 27, **no bypass flags**), android **36.1.118**, maui **10.0.110**. Android API 36 is installed and the default JDK 22 builds.
- All five targets built at commit `5ae3810`: backend, web, `net10.0-ios`, `net10.0-android`, `net10.0-maccatalyst`. **Every one of these must still build at the end of every WP merge.** "Blocked on toolchain" is not an acceptable exit.
- Minimum OS (already in the MAUI csproj): iOS 15.0, MacCatalyst 17.0, Android 24.
- The system dotnet folder is root-owned, so agents cannot install SDKs or workloads. If something is missing, stop and report it in your notes file.
- Go, Docker and Redis are not installed and are not needed.
- `global.json` currently pins `9.0.0`/`latestMajor`. WP0 replaces it (§3.1).

### 0.2 Repo state
- Commit `5ae3810` already removed Counter, Weather, DbTest, SignalRTest, AuthTest, TestHub, Stripe, Domain.Shared (tracked), and the Windows and Tizen targets. **Do not re-plan that cleanup.** What is still present and must go is listed in §3.4.
- Tracked projects today: `BackendWebAPI/` (old server; fully replaced), `TicTacToeUltimate.Shared/` (RCL), `TicTacToeUltimate.Web/` (host, currently InteractiveServer+WASM), `TicTacToeUltimate.Web.Client/` (WASM), `TicTacToeUltimate/` (MAUI, TFMs android/ios/maccatalyst, references `Plugin.AdMob`).
- Ports already in use by launch settings and kept: **Server http 5091 / https 7086**, **Web http 5028 / https 7008**.
- Design inputs currently live only in the session scratchpad. WP0 copies them into the repo under `/design` (§3.4). After that, implementers read **only** the repo copies.
  - `…/scratchpad/ds/project/{tokens.json,README.md,design-system.json,components/**}` → `/design/system/`
  - `…/scratchpad/canvas/project/*.dc.html` → `/design/mockups/`
  - `…/scratchpad/verify_solve.py`, `…/scratchpad/variants/3x3/{solve3.py,results.txt}` → `/tools/study/` (reference only, not built)

  Full scratchpad path: `/private/tmp/claude-501/-Users-ali-RiderProjects-TicTacToeUltimate/ec628b3a-bc0d-4f1a-be21-b5be550ace9d/scratchpad/`.

### 0.3 Facts that are test oracles
From the variant study (`tools/study/results.txt`, two independent solvers). The task text says "~167k states". That figure is **wrong**; use these numbers.

| Ruleset (3x3, K=3) | reachable | non-terminal | start value | start dist | notes |
|---|---|---|---|---|---|
| FIFO W=7, vacated playable, vanishing piece CAN win (legacy code behaviour, `w1`) | 368,074 | 265,402 | X wins | 9 | centre opening: X wins in 8 more plies; the other 8 openings: O wins in 9. Pie: second player (chooser) wins |
| FIFO W=7, vacated NOT playable | 357,130 | 254,458 | draw | – | |
| FIFO W=5, vacated playable | 33,850 | 30,970 | X wins | 7 | |
| FIFO W=6, vacated playable | 128,170 | 116,074 | draw | – | |
| FIFO W=6, vacated NOT playable | 128,170 | 116,074 | X wins | 13 | |
| FIFO W=8, vacated playable | 597,178 | 422,650 | draw | – | |
| FIFO W=9, vacated playable | 596,026 | 363,898 | X wins | 11 | |
| FIFO W=9, vacated NOT playable | 549,946 | 340,858 | draw | – | 46,080 "stuck" states (no legal move → Draw) |
| Per-player cap 3, vacated playable | 128,170 | 116,074 | draw | – | equivalent to FIFO W=6 |
| Per-player cap 4, vacated NOT playable | 549,946 | 375,418 | X wins | 11 | |
| FIFO W=7, ban centre on move 1 | 360,137 | 258,201 | **O wins** | 10 | |
| Per-player cap 3, vacated NOT playable, any visible line wins (**Default** since 2026-10-15 playtest, `n3k3-cap3-v0-w1`) | 128,170 | 116,074 | X wins | 13 | openings: edges (1,3,5,7) X wins in 12; centre and corners draw. Pie: **draw** |
| FIFO W=7, vacated playable, vanishing piece cannot win (superseded default, `w0` "ghost" rule; Arena board) | 369,802 | 331,066 | X wins | 13 | openings: edges (1,3,5,7) X wins in 12; centre and corners (0,2,4,6,8) draw (perfect play cycles forever; the clock decides). Draw positions 11.643%. Pie: **draw** (X opens a corner/centre) |

"start dist" is plies to the forced end from the empty board, with perfect play by both sides (winner fastest, loser slowest).

Hand-verified rule cases (Default ruleset = vanishing piece cannot win, verified with a Python reference on 2026-10-15; cells are row-major 0..8):

| actions | result |
|---|---|
| `4,0,8,2,6,3,5` | ongoing, O to move, ply 7, board `O.OOXXX.X`, nextToVanish 4, legal `[1,4,7]` |
| `4,0,8,2,6,3,5,1` | ongoing (NOT a win): X4 vanished, O holds 0,1,2,3 but O's own piece on 0 is next to vanish so it cannot count; X to move, ply 8, board `OOOO.XX.X`, nextToVanish 0, legal `[0,4,7]`. Under the legacy `w1` rules this is **O wins** with winLine `[0,1,2]` |
| `4,0,8,2,6,3,5,1,7` | ongoing: X holds 5,6,7,8 but 8 is next to vanish, so 6-7-8 does not count; O to move, ply 9, board `.OOO.XXXX`, nextToVanish 8, legal `[0,4,8]` |
| `0,4,1,8,2` | **X wins** at ply 5 (window not full, nothing excluded), winLine `[0,1,2]` |
| `4,0,8,2,6,3,5,7` | ongoing, X to move, board `O.OO.XXOX`, nextToVanish 0, legal `[0,1,4]` |
| `4,0,8,2,6,3,5,4` | legal (vacated cell replayed): board `O.OOOXX.X`, X to move, legal `[0,1,7]` |
| same, with vacated NOT playable | the 8th action fails with `CellJustVacated` |
| `4,4` | the 2nd action fails with `Occupied` |

Clock oracles (§4.5): ×1: 0s→15, 59.999s→15, 60s→10, 71.999s→10, 72s→9, 84s→8, 91s→8 (next: 7 at 96s, "in 5s"), 156s→2, 3600s→2. ×2: 0→30, 119.999→30, 120→20, 144→18, 312→4 (floor). ×4: 0→60, 240→40, floor 8 from 624s.

Glicko-2 oracle (Glickman's paper, τ=0.5): 1500/200/0.06 vs (1400/30 win, 1550/100 loss, 1700/300 loss) → 1464.06 / 151.52 / 0.05999.

---

## 1. Decisions (binding for implementers; WP0 appends the list to AGENTS.md)

Items marked **[OPEN]** have a working default so nobody is blocked; the owner may change them later through config.

- **D1 Topology.** The authoritative game server stays a **separate ASP.NET Core host**, `TicTacToeUltimate.Server`, which replaces `BackendWebAPI`. `TicTacToeUltimate.Web` only serves the Blazor WASM app. Web (browser) and MAUI both connect to the server URL from config. CORS is enabled for the web origins. A web deploy never restarts live matches.
- **D2 Web render mode.** Global `InteractiveWebAssembly` with `prerender: false`. There is no Interactive Server, so there is no circuit double hop to distort RTT.
- **D3 One engine everywhere.** `TicTacToeUltimate.Engine` is pure and deterministic, with no package dependencies. It is used by the server (authority), the client (derived UI only: faded piece, legal hints, threat hints, replay), the RulesTool and the tests. Go conforms through the JSON vectors.
- **D4 Ruleset.** **Superseded 2026-10-15 (playtest):** the default is now `n3k3-cap3-v0-w1-none` / Ranked+Daily `n3k3-cap3-v0-w1-pie`: each player holds at most 3 pieces, placing a 4th removes your own oldest first, that cell can't be replayed this turn, any visible 3-in-a-row wins (solved: X wins in 13, edges win, corners/centre draw, pie = draw). The `w0` ghost interpretation below was a misunderstanding of the owner's rule; it remains available (Arena board, Offline "Legacy 7-piece"). Former text: the default was 3x3, K=3, global FIFO W=7, vacated cell playable, **the piece about to vanish cannot count toward a win** (`VanishingPieceCanWin=false`, id segment `w0`). The old BackendWebAPI engine let it count; that was a bug. **Ranked and Daily** add the pie rule (`n3k3-fifo7-v1-w0-pie`). Casual, Unlimited and Challenge use the default with no opening rule. Everything is configurable per mode (§6.10). With pie, perfect play is a draw (endless cycle), so ranked games are decided by skill under the clock. 4x4 variants remain under study; 3x3 is final for launch.
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

---

## 2. Solution layout

### 2.1 Tree (root-level projects; no `src/` move, so churn stays minimal)
```
/global.json                       { "sdk": { "version": "10.0.100", "rollForward": "latestFeature", "allowPrerelease": false } }
/Directory.Build.props             net10.0 defaults (§3.2)
/Directory.Packages.props          central package versions (§3.3), WP0 only
/TicTacToeUltimate.sln             all projects
/TicTacToeUltimate.Web.slnf        all except TicTacToeUltimate (MAUI)
/AGENTS.md  /README.md
/docs/ARCHITECTURE.md  /docs/rules.md (WP1)  /docs/notes/WP*.md  /docs/contract-requests/*.md
/design/system/**  /design/mockups/*.dc.html                      (WP0 copies; read-only afterwards)
/spec/rules/vectors/*.json  /spec/rules/summary.json  /spec/rules/*.schema.json   (WP1)
/spec/clock/schedule.json                                          (WP1)
/spec/bot-protocol.md                                              (WP4)
/supabase/config.toml  /supabase/migrations/*.sql  /supabase/seed.sql            (WP3)
/scripts/dev.sh  /scripts/build-all.sh                             (WP0)
/tools/TicTacToeUltimate.RulesTool/                                (WP1)
/tools/study/                                                      (WP0 copy, reference only)

/TicTacToeUltimate.Engine/          net10.0 classlib, zero package deps             ns TicTacToeUltimate.Engine[.Clocks|.Analysis]
/TicTacToeUltimate.Contracts/       net10.0 classlib, refs Engine                   ns TicTacToeUltimate.Contracts[.Bot]
/TicTacToeUltimate.Server/          Microsoft.NET.Sdk.Web host, refs Contracts       ns TicTacToeUltimate.Server.*
/TicTacToeUltimate.Client/          net10.0 classlib (non-UI client core), refs Contracts, SignalR.Client   ns TicTacToeUltimate.Client.*
/TicTacToeUltimate.Shared/          Razor class library (all UI), refs Client          ns TicTacToeUltimate.Shared.*
/TicTacToeUltimate.Web/             Blazor Web App host (static shell + WASM), refs Web.Client
/TicTacToeUltimate.Web.Client/      Blazor WASM entry, refs Shared
/TicTacToeUltimate/                 .NET MAUI Blazor Hybrid: net10.0-android;net10.0-ios;net10.0-maccatalyst, refs Shared

/tests/TicTacToeUltimate.Engine.Tests/
/tests/TicTacToeUltimate.Server.Tests/              unit tests (FakeTimeProvider), per-WP subfolders
/tests/TicTacToeUltimate.Server.IntegrationTests/   WebApplicationFactory + real SignalR/WebSocket clients
/tests/TicTacToeUltimate.Client.Tests/
/tests/TicTacToeUltimate.Shared.Tests/              bUnit
```

### 2.2 Reference graph (acyclic)
```
Engine <- Contracts <- Server
Engine <- Contracts <- Client <- Shared <- Web.Client <- Web
                                  Shared <- TicTacToeUltimate (MAUI)
RulesTool -> Engine
Engine.Tests -> Engine ; Server.Tests -> Server ; Server.IntegrationTests -> Server ; Client.Tests -> Client ; Shared.Tests -> Shared
```
Server does **not** reference Shared, Client or Web. Shared does **not** reference Server.

---

## 3. WP0 foundation details

### 3.1 global.json
```json
{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature", "allowPrerelease": false } }
```

### 3.2 Directory.Build.props
```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Deterministic>true</Deterministic>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest</AnalysisLevel>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <NoWarn>$(NoWarn);NU1603;NU1701;CS1591</NoWarn>
  </PropertyGroup>
  <PropertyGroup Condition="'$(MSBuildProjectName)' == 'TicTacToeUltimate.Engine' or '$(MSBuildProjectName)' == 'TicTacToeUltimate.Contracts'">
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsTrimmable>true</IsTrimmable>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
</Project>
```
Projects set their own `TargetFramework(s)`. The MAUI project keeps `RestorePackagesWithLockFile`; WP0 regenerates `packages.lock.json` with `dotnet restore --force-evaluate`.

### 3.3 Directory.Packages.props (WP0 owns; nobody else adds versions)
Pin every package below. Use the versions already in the repo where they exist (`10.0.13` for Microsoft.AspNetCore.* and Microsoft.Extensions.*). For the others, use the latest stable on nuget.org at WP0 time and record the exact version.

| Package | Used by |
|---|---|
| Microsoft.AspNetCore.Authentication.JwtBearer 10.0.13 | Server |
| Microsoft.AspNetCore.OpenApi 10.0.13 | Server (Development only) |
| Microsoft.AspNetCore.SignalR.Client 10.0.13 | Client, Server.IntegrationTests |
| Microsoft.AspNetCore.Components.Web 10.0.13 | Shared |
| Microsoft.AspNetCore.Components.WebAssembly 10.0.13 | Web.Client |
| Microsoft.AspNetCore.Components.WebAssembly.Server 10.0.13 | Web |
| Microsoft.Extensions.Http 10.0.13 | Client |
| Microsoft.Extensions.Logging.Debug 10.0.13 | MAUI |
| Microsoft.Maui.Controls / Microsoft.AspNetCore.Components.WebView.Maui | MAUI (`Version="$(MauiVersion)"` stays in the csproj via `VersionOverride`) |
| Npgsql (10.x), Dapper (2.1.x) | Server (Postgres repos) |
| Microsoft.Extensions.TimeProvider.Testing (latest 10.x) | Server.Tests, Client.Tests |
| Microsoft.AspNetCore.Mvc.Testing 10.0.13 | Server.IntegrationTests |
| xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | all tests (VSTest runner, `dotnet test`) |
| bunit (2.x) | Shared.Tests |

Removed: Plugin.AdMob, StackExchange.Redis, Npgsql.EntityFrameworkCore.PostgreSQL, Microsoft.EntityFrameworkCore.Design, Swashbuckle.AspNetCore, AspNet.Security.OAuth.Apple, Microsoft.AspNetCore.Authentication.Google.

### 3.4 Cleanup that WP0 performs (what actually remains)
- `git rm -r BackendWebAPI db`. The new `TicTacToeUltimate.Server` is created fresh, so nothing from the old server is kept. Treat `BackendWebAPI/GameEngine/GameEngine.cs` as historical semantics only; the semantics in §4 are normative.
- Delete untracked leftovers on disk: `Domain.Shared/`, `.data/`.
- Shared: `git rm` `Layout/NavMenu.razor*`, `Models/*`, `Pages/GameTest.razor`, `Realtime/GameMessages.cs`, `Services/{ApiClient,GameClient,IFormFactor}.cs`, `wwwroot/bootstrap/`. Replace `Pages/Home.razor` and `Layout/MainLayout.razor*` with placeholders (WP6 owns them afterwards).
- Web: delete `Services/FormFactor.cs` and rewrite `Program.cs` and `Components/App.razor` per §8.6 (a minimal compiling version; WP5 owns them afterwards). Web.Client: delete `Services/FormFactor.cs`.
- MAUI: delete `Services/FormFactor.cs`, `Resources/Images/dotnet_bot.svg` and its `MauiImage Update` line, `Resources/Fonts/OpenSans-Regular.ttf`, the `Plugin.AdMob` reference and the `admob:BannerAd` in `MainPage.xaml` (the WebView fills the page), and the startup probe and LongPolling code in `MauiProgram.cs`.
- Copy the design and study files (§0.2).
- Create every new project, csproj, test project and the stub files listed in §13.1, plus `TicTacToeUltimate.Web.slnf`.
- `.gitignore`: add `.data/`, `**/TestResults/`, `*.DotSettings.user`.

---

## 4. Engine (`/TicTacToeUltimate.Engine`, WP1 implements; WP0 writes the signatures verbatim with `throw new NotImplementedException()` bodies where logic is needed)

Rules for engine code:
- No I/O, no `DateTime`, no `System.Random`, no floating point in rules, no iteration over hash-ordered collections.
- `GameState` is immutable. Cells are row-major `0..N²−1` and 0-based.

### 4.1 Core API (verbatim)
```csharp
namespace TicTacToeUltimate.Engine;

public enum Color : byte { X = 0, O = 1 }
public enum RemovalKind : byte { None = 0, GlobalFifo = 1, PerPlayerCap = 2 }
public enum OpeningRule : byte { None = 0, PieSwap = 1, BanCenter = 2 }
public enum GamePhase : byte { AwaitingPlacement = 0, AwaitingSwapDecision = 1, Ended = 2 }
public enum Outcome : byte { None = 0, XWins = 1, OWins = 2, Draw = 3 }
public enum ActionKind : byte { Place = 0, Swap = 1, KeepSides = 2 }
public enum MoveError : byte { None = 0, GameOver, WrongActionKind, OutOfBounds, Occupied, CellJustVacated, BannedOpening }

/// <summary>Immutable rule configuration. Window = total pieces on board (GlobalFifo) or pieces per player (PerPlayerCap); ignored for None.</summary>
public sealed record RuleSet(
    int BoardSize = 3,
    int WinLength = 3,
    RemovalKind Removal = RemovalKind.GlobalFifo,
    int Window = 7,
    bool VacatedCellPlayable = true,
    bool VanishingPieceCanWin = false,
    OpeningRule Opening = OpeningRule.None)
{
    /// <summary>3x3, K3, FIFO 7, vacated playable, vanishing piece CANNOT win (owner's rule), no opening rule.</summary>
    public static RuleSet Default { get; } = new();
    /// <summary>Default + PieSwap (Ranked/Daily default).</summary>
    public static RuleSet RankedDefault { get; } = new(Opening: OpeningRule.PieSwap);

    public int CellCount => BoardSize * BoardSize;

    /// <summary>Canonical id: "n{N}k{K}-{fifo{W}|cap{W}|none}-v{0|1}-w{0|1}-{none|pie|ban}", e.g. "n3k3-cap3-v0-w1-none".</summary>
    public string Id { get; }   // computed in the record body (not a positional member)

    public static bool TryParse(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RuleSet? rules);

    /// <summary>Empty = valid. Checks 3&lt;=N&lt;=7, 3&lt;=K&lt;=N, GlobalFifo: 2K-1&lt;=Window&lt;=N², PerPlayerCap: K&lt;=Window&lt;=N²/2, None: Window ignored.</summary>
    public IReadOnlyList<string> Validate();
}

public readonly record struct Piece(int Cell, Color Color, int Placement);

public readonly record struct GameAction(ActionKind Kind, int Cell)
{
    public static GameAction Place(int cell) => new(ActionKind.Place, cell);
    public static GameAction Swap { get; } = new(ActionKind.Swap, -1);
    public static GameAction KeepSides { get; } = new(ActionKind.KeepSides, -1);
}

public sealed class GameState
{
    public RuleSet Rules { get; }
    public ImmutableArray<Piece> Pieces { get; }      // on board now, oldest -> newest (placement order)
    public Color ToMove { get; }                      // colour that acts next (also the chooser in AwaitingSwapDecision)
    public int Ply { get; }                           // actions applied, including Swap/KeepSides
    public int Placements { get; }                    // Place actions applied
    public GamePhase Phase { get; }
    public Outcome Outcome { get; }
    public ImmutableArray<int> WinLine { get; }       // empty unless won
    public bool Swapped { get; }                      // pie swap taken: seats exchanged colours
    public int? LastVanishedCell { get; }             // cell removed by the most recent placement
    public Color? At(int cell);
}

public readonly record struct ApplyResult(GameState State, MoveError Error, int? VanishedCell)
{
    public bool Ok => Error == MoveError.None;
}

public sealed class InvalidActionException(int index, MoveError error)
    : Exception($"Action #{index} rejected: {error}")
{
    public int Index { get; } = index;
    public MoveError Error { get; } = error;
}

public static class Rules
{
    public static GameState Initial(RuleSet rules);
    /// <summary>Never throws for bad input; on error returns the unchanged state and the error.</summary>
    public static ApplyResult Apply(GameState state, GameAction action);
    /// <summary>Empty unless Phase == AwaitingPlacement.</summary>
    public static ImmutableArray<int> LegalCells(GameState state);
    /// <summary>Place actions for LegalCells, or [Swap, KeepSides] in AwaitingSwapDecision, or empty.</summary>
    public static ImmutableArray<GameAction> LegalActions(GameState state);
    /// <summary>Cell whose piece is removed when ToMove next places; null if none.</summary>
    public static int? NextToVanish(GameState state);
    /// <summary>Cells where colour c would win if it placed there now (vanish-aware). Used for threat hints.</summary>
    public static ImmutableArray<int> ImmediateWins(GameState state, Color c);
    /// <summary>Fold of Apply; throws InvalidActionException on the first illegal action.</summary>
    public static GameState Replay(RuleSet rules, IEnumerable<GameAction> actions);
}

public static class Notation
{
    /// <summary>Tokens joined by ',': a cell number for Place, "swap", "keep". Example "4,0,swap,8".</summary>
    public static string Format(IEnumerable<GameAction> actions);
    public static ImmutableArray<GameAction> Parse(string text);   // "" -> empty
    /// <summary>N² chars, row-major: 'X', 'O' or '.'.</summary>
    public static string Board(GameState state);
}

public static class LineTable
{
    /// <summary>All K-in-a-row lines (rows, columns, both diagonal directions), cached per (n, k), in a deterministic order.</summary>
    public static ImmutableArray<ImmutableArray<int>> For(int boardSize, int winLength);
}

/// <summary>Deterministic RNG for casual auto-moves and colour assignment. Never use System.Random for game logic.</summary>
public struct SplitMix64
{
    public SplitMix64(ulong seed);
    public ulong Next();
    /// <summary>Uniform in [0, maxExclusive) by rejection sampling.</summary>
    public int NextInt(int maxExclusive);
}
```

### 4.2 Normative semantics of `Rules.Apply` (also written to `docs/rules.md` by WP1)
1. If `Phase == Ended`, return `GameOver`.
2. **Swap phase.** If `Phase == AwaitingSwapDecision`: only `Swap`/`KeepSides` are allowed (anything else returns `WrongActionKind`). Then `Ply++`, `Phase = AwaitingPlacement`, `Swapped = (Kind == Swap)`, and `ToMove` is unchanged (still colour O). The board is unchanged. Then step 9.
3. In `AwaitingPlacement`, only `Place` is allowed (otherwise `WrongActionKind`). Bounds: a cell outside `[0, N²)` returns `OutOfBounds`.
4. **Removal (before placement):**
   - GlobalFifo: if `Pieces.Length == Window`, `removed = Pieces[0]`.
   - PerPlayerCap: if the mover holds `Window` pieces, `removed` = the mover's oldest piece.
   - None: no removal.
5. **Legality, on the board without `removed`:**
   - The cell is occupied: `Occupied`.
   - `!VacatedCellPlayable && cell == removed.Cell`: `CellJustVacated`.
   - `Opening == BanCenter && Placements == 0` and the cell is a centre cell: `BannedOpening`. For odd N the centre is the single middle cell; for even N it is the central 2×2.
6. Commit: drop `removed`, append `Piece(cell, ToMove, Placements)`, `Placements++`, `Ply++`, `LastVanishedCell = removed?.Cell`.
7. **Win check, for the mover only.**
   - The mover's pieces are taken after the placement.
   - If `!VanishingPieceCanWin`, exclude `NextToVanish(stateAfterPlacement)` when that piece belongs to the mover.
   - If any line of `LineTable.For(N, K)` is fully covered by those pieces, set `Outcome = XWins/OWins`, `WinLine` = the first such line in LineTable order, `Phase = Ended`, and return.
8. **Pie trigger.** If `Opening == PieSwap && Placements == 1`, then `ToMove = O`, `Phase = AwaitingSwapDecision`; return.
9. Otherwise `ToMove` = the other colour. (After step 2, ToMove stays O.)
10. **No legal move.** If `Phase == AwaitingPlacement` and `LegalCells` is empty, the result is `Outcome = Draw`, `Phase = Ended`.

`NextToVanish` (state with Phase AwaitingPlacement):
- GlobalFifo: `Pieces[0].Cell` if `Pieces.Length == Window`.
- PerPlayerCap: the oldest piece of `ToMove` if it holds `Window` pieces.
- Otherwise null.

With odd W under GlobalFifo, the vanishing piece is always the opponent's. That is the AGENTS phrase "the opponent's oldest piece vanishes", and it holds only for odd W.

### 4.3 Clocks (verbatim; pure)
```csharp
namespace TicTacToeUltimate.Engine.Clocks;

public enum TimeControl : byte { Ranked = 0, CasualX2 = 1, CasualX4 = 2, Unlimited = 3, Daily = 4 }

public static class MoveClock
{
    public static readonly TimeSpan DailyPerMove = TimeSpan.FromHours(24);

    /// <summary>1 for Ranked, 2 for CasualX2, 4 for CasualX4, 0 otherwise.</summary>
    public static int Scale(TimeControl tc);

    /// <summary>Allowance for a turn that STARTS at gameElapsed. null for Unlimited; DailyPerMove for Daily.
    /// Sudden-death (s = Scale): e &lt; 60s·s -> 15s·s; else max(2s·s, 10s·s - floor((e-60s·s)/(12s·s))·1s·s).</summary>
    public static TimeSpan? Allowance(TimeControl tc, TimeSpan gameElapsed);

    /// <summary>True for scaled sudden-death controls once gameElapsed &gt;= 60s·s.</summary>
    public static bool IsSuddenDeath(TimeControl tc, TimeSpan gameElapsed);

    /// <summary>Next point (in game-elapsed time) where the allowance changes, and the new allowance; null if none (floor reached, Unlimited, Daily).</summary>
    public static (TimeSpan AtGameElapsed, TimeSpan Allowance)? NextStep(TimeControl tc, TimeSpan gameElapsed);
}
```

### 4.4 Tablebase and analysis (verbatim)
```csharp
namespace TicTacToeUltimate.Engine.Analysis;

public enum Value : sbyte { Loss = -1, Draw = 0, Win = 1 }                 // side-to-move perspective
public readonly record struct PositionValue(Value Value, int Distance);   // plies to forced end; 0 for Draw and terminal
public enum MoveClass : byte { Best = 0, Good = 1, Blunder = 2 }
// Best: keeps the value with optimal distance (fastest win / slowest loss / any draw-keeping move).
// Good: keeps the value but not optimal distance. Blunder: value worsens for the mover (Win->Draw/Loss, Draw->Loss).

public sealed record MoveEval(GameAction Action, Value ResultForMover, int Distance, MoveClass Class);
public sealed record PlyAnalysis(int Ply, GameAction Played, PositionValue Before, MoveClass Class, ImmutableArray<GameAction> BestActions);

public sealed class TablebaseTooLargeException(string rulesId, long limit) : Exception($"{rulesId} exceeds {limit} states");

public sealed class Tablebase
{
    /// <summary>True when the state key fits 64 bits: N² &lt;= 16 and piece capacity fits (FIFO: Window, Cap: 2·Window, None: N²) with 4 bits/cell + 5-bit count + 1 to-move bit (cap packs two queues).</summary>
    public static bool CanBuild(RuleSet rules);
    /// <summary>Forward BFS from Initial over AwaitingPlacement positions (pie ignored), then counter-based retrograde labelling; unresolved = Draw.</summary>
    public static Tablebase Build(RuleSet rules, long maxStates, CancellationToken ct);
    public RuleSet Rules { get; }
    public int StateCount { get; }          // reachable incl. terminal; Default = 369,802
    public int NonTerminalCount { get; }    // Default = 331,066
    /// <summary>For AwaitingPlacement: value for ToMove. For AwaitingSwapDecision: value for the chooser if it keeps (= value of the position for O to place). Ended: (Loss,0) for ToMove if won by the opponent, (Draw,0) for Draw.</summary>
    public PositionValue Evaluate(GameState state);
    /// <summary>Per legal action. Swap-phase: Keep = Evaluate(state); Swap = negated value (chooser becomes the other side).</summary>
    public ImmutableArray<MoveEval> EvaluateMoves(GameState state);
}

public static class GameAnalyzer
{
    public static ImmutableArray<PlyAnalysis> Analyze(Tablebase tablebase, RuleSet rules, IReadOnlyList<GameAction> actions);
}
```
The state key is ruleset-independent within a `Tablebase` (the ruleset is fixed per table). Storage uses sorted `ulong[]` keys plus parallel packed values (`sbyte` value, `ushort` distance) with binary search, or a `Dictionary<ulong,int>`; this is WP1's choice. A **build-time guard test** requires the Default table to build in under 3 s.

Oracles (Default = `n3k3-cap3-v0-w1-none`; the legacy `w0` table has the same start/opening values, see §0.3 and the §7.2 tablebase block): `Evaluate(Initial(Default)) == (Win, 13)`; after `1`: `(Loss, 12)` for O; after `4`: `(Draw, 0)`; after `0`: `(Draw, 0)`. Draw values come from cycles (positions never resolved by retrograde analysis), so the table and `GameAnalyzer` must carry `Draw` and must not assume every position is decided. Legacy `w1` (`n3k3-fifo7-v1-w1-none`): start `(Win, 9)`; after `4`: `(Loss, 8)` for O; after `0`: `(Win, 9)` for O.

---

## 5. Contracts (`/TicTacToeUltimate.Contracts`, WP0 writes verbatim; frozen)

General rules:
- Everything is a `sealed record`. Wire JSON is camelCase with string enums and nulls omitted.
- Times on the wire are **Unix epoch milliseconds of the server clock** (`long …Ms`) unless the type is `DateTimeOffset`; REST responses use ISO-8601 for `DateTimeOffset`.
- Cells are 0-based row-major ints.
- Contracts references Engine **only** for the enums `Color, Outcome, GamePhase, ActionKind, RemovalKind, OpeningRule, TimeControl, Value, MoveClass`. Records never embed engine records; `RuleSetDto` maps explicitly.

### 5.1 Enums, constants, catalog
```csharp
namespace TicTacToeUltimate.Contracts;

public enum MatchMode { Ranked, Casual, Unlimited, Daily, Challenge }
public enum EndReason { Line, Timeout, Resign, Abandoned, Draw, Aborted }
public enum RatingPool { Ranked, CasualX2, CasualX4, Unlimited, Daily }
public enum Tier { Bronze, Silver, Gold, Platinum, Diamond, Master }
public enum PresenceStatus { Offline, Online, InGame }
public enum AccountKind { Guest, Account, Bot }
public enum ClientPlatform { Web, iOS, Android, MacCatalyst }
public enum LeaderboardScope { Global, Friends, Country, MasterSeason }
public enum RewardKind { StreakFreeze, ExtraQuest, Analysis }
public enum ChallengeStatus { Open, Accepted, Declined, Cancelled, Expired }
public enum FriendRequestDirection { Incoming, Outgoing }
public enum FriendStatus { Self, None, Friends, RequestSent, RequestReceived, Blocked }
public enum GameResultForUser { Win, Loss, Draw, Aborted }
public enum NotificationKind { DailyYourTurn, Promotion, Demotion, ChallengeExpired, Info }

public static class ProtocolInfo { public const int Version = 1; }

public static class HubRoutes { public const string Game = "/hubs/game"; }
public static class BotRoutes { public const string WebSocket = "/bot/ws"; }

public static class RatingPools
{
    public static RatingPool For(TimeControl tc) => tc switch
    {
        TimeControl.Ranked => RatingPool.Ranked, TimeControl.CasualX2 => RatingPool.CasualX2,
        TimeControl.CasualX4 => RatingPool.CasualX4, TimeControl.Unlimited => RatingPool.Unlimited,
        _ => RatingPool.Daily,
    };
    public static bool IsVisible(RatingPool p) => p is RatingPool.Ranked or RatingPool.Daily;
}

public static class ErrorCodes
{
    public const string NotYourTurn = "not_your_turn", StalePly = "stale_ply", IllegalMove = "illegal_move",
        Occupied = "occupied", CellJustVacated = "cell_just_vacated", BannedOpening = "banned_opening",
        WrongActionKind = "wrong_action_kind", MatchNotFound = "match_not_found", MatchOver = "match_over",
        NotAParticipant = "not_a_participant", SpectateNotAllowed = "spectate_not_allowed", RateLimited = "rate_limited",
        GuestNotAllowed = "guest_not_allowed", UsernameRequired = "username_required", AlreadyInQueue = "already_in_queue",
        AlreadyInMatch = "already_in_match", DailyLimit = "daily_limit", ChallengeNotFound = "challenge_not_found",
        ChallengeExpired = "challenge_expired", ChallengeTaken = "challenge_taken", CannotChallengeSelf = "cannot_challenge_self",
        NotFriends = "not_friends", Blocked = "blocked", UsernameTaken = "username_taken", UsernameInvalid = "username_invalid",
        NoRenamesLeft = "no_renames_left", TosRequired = "tos_required", AnalysisWarming = "analysis_warming",
        AnalysisUnavailable = "analysis_unavailable", RewardRequired = "reward_required", RewardInvalid = "reward_invalid",
        RewardCapped = "reward_capped", RematchNotAllowed = "rematch_not_allowed", EmoteUnknown = "emote_unknown",
        Timeout = "timeout", ProtocolVersion = "protocol_version", Validation = "validation", Internal = "internal";

    public static string FromMoveError(MoveError e) => e switch
    {
        MoveError.Occupied => Occupied, MoveError.CellJustVacated => CellJustVacated,
        MoveError.BannedOpening => BannedOpening, MoveError.WrongActionKind => WrongActionKind,
        MoveError.GameOver => MatchOver, _ => IllegalMove,
    };
}

public sealed record EmoteDto(string Id, string Kind /* "emote" | "phrase" */, string Text);

public static class EmoteCatalog
{
    public static readonly IReadOnlyList<EmoteDto> All =
    [
        new("gg", "emote", "🤝"), new("laugh", "emote", "😂"), new("wow", "emote", "😮"), new("think", "emote", "🤔"),
        new("fire", "emote", "🔥"), new("cry", "emote", "😭"), new("angry", "emote", "😤"), new("cool", "emote", "😎"),
        new("hi", "phrase", "Hi!"), new("glhf", "phrase", "Good luck!"), new("wp", "phrase", "Well played!"),
        new("nice", "phrase", "Nice move!"), new("oops", "phrase", "Oops!"), new("thanks", "phrase", "Thanks!"),
        new("hurry", "phrase", "Tick tock…"), new("rematch", "phrase", "Rematch?"),
    ];
    public static bool IsKnown(string id) => All.Any(e => e.Id == id);
}
```

### 5.2 Rules, players, profile, progression DTOs
```csharp
namespace TicTacToeUltimate.Contracts;

public sealed record RuleSetDto(string Id, int BoardSize, int WinLength, RemovalKind Removal, int Window,
    bool VacatedCellPlayable, bool VanishingPieceCanWin, OpeningRule Opening)
{
    public static RuleSetDto From(RuleSet r) => new(r.Id, r.BoardSize, r.WinLength, r.Removal, r.Window,
        r.VacatedCellPlayable, r.VanishingPieceCanWin, r.Opening);
    public RuleSet ToEngine() => new(BoardSize, WinLength, Removal, Window, VacatedCellPlayable, VanishingPieceCanWin, Opening);
}

public sealed record TierDto(Tier Tier, int Division /* 3..1; 0 for Master */, int PointsToNext /* 0 for Master */, double Progress /* 0..1 inside division */);
public sealed record TierChangeDto(TierDto Before, TierDto After, bool Promoted, bool Demoted);
public sealed record RatingDto(RatingPool Pool, int Rating, int Rd, int Games, bool Provisional, TierDto? Tier, int WeekDelta);
public sealed record RatingChangeDto(RatingPool Pool, int Before, int After, int Delta, bool Provisional);

public sealed record PlayerCardDto(Guid UserId, string DisplayName, string AvatarId, string? Country, AccountKind Kind,
    TierDto? Tier, int? Rating, bool Provisional);

public sealed record MeDto(Guid UserId, AccountKind Kind, string DisplayName, string? Username, bool NeedsUsername,
    string AvatarId, string? Country, string TimeZone, int RenamesLeft, RatingDto[] Ratings /* visible pools only */);

public sealed record ProfileStatsDto(int GamesPlayed, int Wins, int Losses, int Draws, double WinRate,
    int CurrentStreak, int BestStreak, int DailyInProgress);
public sealed record ProfileDto(PlayerCardDto Card, DateTimeOffset JoinedAt, RatingDto[] Ratings, ProfileStatsDto Stats,
    GameSummaryDto[] RecentGames, FriendStatus FriendStatus);

public sealed record StreakDto(int Current, int Best, int FreezesBanked, bool SecuredToday, bool AtRisk);
public sealed record QuestDto(string QuestId /* "win1"|"win3"|"win5"|"bonus" */, string Title, int Progress, int Target, bool Completed, bool Bonus);
public sealed record ProgressDto(StreakDto Streak, QuestDto[] Quests, DateOnly Day, long ResetsAtMs, bool ExtraQuestAvailable);

public sealed record TierBandDto(Tier Tier, int MinRating, int DivisionWidth);
public sealed record AdConfigDto(int InterstitialEveryGames, bool SkipAfterTwoLosses);
public sealed record ModeConfigDto(TimeControl TimeControl, MatchMode Mode, RuleSetDto Rules, bool Rated, bool GuestsAllowed);
public sealed record AppConfigDto(int ProtocolVersion, bool DevAuth, bool SupabaseAuth, string? SupabaseUrl, string? SupabaseAnonKey,
    ModeConfigDto[] Modes, EmoteDto[] Emotes, TierBandDto[] Tiers, int LeaderboardMinGames, AdConfigDto Ads, string WebBaseUrl);
```

### 5.3 Auth DTOs (REST)
```csharp
namespace TicTacToeUltimate.Contracts;

public sealed record DevUserDto(Guid UserId, string DisplayName, AccountKind Kind, TierDto? Tier, int? Rating);
public sealed record DevTokenRequestDto(Guid? UserId, string? NewUsername);     // exactly one set
public sealed record GuestTokenRequestDto(string DeviceId);                       // client-generated stable Guid string
public sealed record TokenResponseDto(string AccessToken, long ExpiresAtMs, MeDto Me);
public sealed record SetUsernameDto(string Username, bool AcceptTos);
public sealed record UpdateProfileDto(string? AvatarId, string? Country, string? TimeZone);
```

### 5.4 Match DTOs and events
```csharp
namespace TicTacToeUltimate.Contracts;

public sealed record SeatDto(int Seat /* 0 = A (starts as X), 1 = B */, PlayerCardDto Player, Color Color, bool Connected, int? RttMs);

public sealed record ClockDto(
    TimeControl TimeControl,
    long? TurnStartedAtMs,            // server epoch ms; null when no turn is running (ended / unlimited before start)
    long? DeadlineAtMs,               // nominal: TurnStartedAtMs + AllowanceMs; null for Unlimited
    int? AllowanceMs,
    int GameElapsedMsAtTurnStart,     // sum of credited ThinkMs of completed turns
    bool SuddenDeath,
    int? NextAllowanceMs,
    int? NextStepAtGameElapsedMs,     // drives "window 8s -> 7s in 5s"
    int[] LagBudgetLeftMs);           // [seatA, seatB]

public sealed record ActionDto(int Ply, int Seat, ActionKind Kind, int? Cell, Color? Color /* null for swap decisions */,
    int? VanishedCell, int ThinkMs, bool Auto);

public sealed record MatchResultDto(Outcome Outcome, int? WinnerSeat, EndReason Reason, int[] WinLine, bool Rated, string? Detail);

public sealed record MatchSnapshotDto(
    Guid MatchId, long Seq, MatchMode Mode, TimeControl TimeControl, bool Rated, RuleSetDto Rules,
    SeatDto[] Seats, bool Swapped, ActionDto[] Actions, string Board, int Ply, GamePhase Phase,
    Color ToMove, int? ToMoveSeat, int[] LegalCells, int? NextToVanish, ClockDto? Clock, MatchResultDto? Result,
    int? YourSeat /* null = spectator */, int SpectatorCount, bool RematchAllowed, long ServerNowMs);

public sealed record MatchEventEnvelope(Guid MatchId, long Seq, long ServerTimeMs, MatchEvent Event);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(ActionApplied), "action")]
[JsonDerivedType(typeof(TurnStarted), "turn")]
[JsonDerivedType(typeof(MatchEnded), "end")]
[JsonDerivedType(typeof(SeatConnectionChanged), "conn")]
[JsonDerivedType(typeof(EmoteShown), "emote")]
[JsonDerivedType(typeof(RematchOffered), "rematchOffer")]
[JsonDerivedType(typeof(RematchStarted), "rematch")]
[JsonDerivedType(typeof(SpectatorCountChanged), "spec")]
public abstract record MatchEvent;
public sealed record ActionApplied(ActionDto Action, string Board, int? NextToVanish, bool Swapped, SeatDto[] Seats) : MatchEvent;
public sealed record TurnStarted(int Ply, Color ToMove, int ToMoveSeat, GamePhase Phase, ClockDto? Clock, int[] LegalCells) : MatchEvent;
public sealed record MatchEnded(MatchResultDto Result) : MatchEvent;
public sealed record SeatConnectionChanged(int Seat, bool Connected, int? RttMs) : MatchEvent;
public sealed record EmoteShown(int Seat, string EmoteId) : MatchEvent;
public sealed record RematchOffered(int Seat) : MatchEvent;
public sealed record RematchStarted(Guid NewMatchId) : MatchEvent;
public sealed record SpectatorCountChanged(int Count) : MatchEvent;

public sealed record SubmitActionDto(Guid MatchId, int ExpectedPly, ActionKind Kind, int? Cell, Guid ActionId);
public sealed record MoveAckDto(bool Accepted, string? Error, long Seq, int CurrentPly);
public sealed record CommandAckDto(bool Ok, string? Error);

public sealed record PostGameSummaryDto(Guid MatchId, MatchResultDto Result, GameResultForUser YourResult,
    RatingChangeDto? Rating, TierChangeDto? Tier, int WinStreak, ProgressDto? Progress,
    bool AnalysisAvailable, int? YourBlunders, bool RematchAllowed);

public sealed record QueueRequestDto(TimeControl TimeControl);
public sealed record QueueTicketDto(bool Accepted, string? Error, TimeControl TimeControl, long EnqueuedAtMs);
public sealed record QueueStatusDto(TimeControl TimeControl, int WaitedMs, int RatingWindow, bool GuestPool);
public sealed record MatchFoundDto(Guid MatchId, MatchMode Mode, TimeControl TimeControl, PlayerCardDto Opponent, Color YourColor, int YourSeat);

public sealed record ClientHelloDto(int ProtocolVersion, string ClientVersion, ClientPlatform Platform, string TimeZone, string? Locale);
public sealed record ServerHelloDto(MeDto Me, long ServerNowMs, Guid[] ActiveMatchIds, int DailyYourTurn, ChallengeDto[] PendingChallenges);

public sealed record DailyGameDto(Guid MatchId, PlayerCardDto Opponent, Color YourColor, bool YourTurn, long? DeadlineAtMs, int Ply);
public sealed record LiveMatchDto(Guid MatchId, TimeControl TimeControl, PlayerCardDto[] Players, int Ply, int SpectatorCount);
```

### 5.5 History, analysis, leaderboards, social, challenges, rewards, bots, notifications
```csharp
namespace TicTacToeUltimate.Contracts;

public sealed record GameSummaryDto(Guid GameId, MatchMode Mode, TimeControl TimeControl, PlayerCardDto Opponent,
    Color YourColor, GameResultForUser Result, EndReason Reason, int? RatingDelta, int Plies, DateTimeOffset EndedAt);
public sealed record GameRecordDto(Guid GameId, MatchMode Mode, TimeControl TimeControl, bool Rated, RuleSetDto Rules,
    SeatDto[] Seats, bool Swapped, ActionDto[] Actions, MatchResultDto Result, DateTimeOffset StartedAt, DateTimeOffset EndedAt);
public sealed record PositionValueDto(Value Value, int Distance);
public sealed record PlyAnalysisDto(int Ply, int Seat, MoveClass Class, PositionValueDto Before, string[] BestActions /* Notation tokens */);
public sealed record GameAnalysisDto(Guid GameId, string RulesId, PlyAnalysisDto[] Plies, int[] BlundersBySeat);

public sealed record LeaderboardRowDto(int Rank, PlayerCardDto Player, int Rating, int Games);
public sealed record LeaderboardPageDto(RatingPool Pool, LeaderboardScope Scope, string? Country, int Page, int PageSize,
    int TotalCount, LeaderboardRowDto[] Rows, LeaderboardRowDto? Me);

public sealed record PresenceDto(Guid UserId, PresenceStatus Status, Guid? MatchId);
public sealed record FriendDto(PlayerCardDto Player, PresenceDto Presence, int UnreadMessages, DateTimeOffset? LastSeenAt);
public sealed record FriendRequestDto(Guid RequestId, PlayerCardDto From, PlayerCardDto To, FriendRequestDirection Direction, DateTimeOffset CreatedAt);
public sealed record FriendsOverviewDto(FriendDto[] Friends, FriendRequestDto[] Requests, PlayerCardDto[] Blocked);
public sealed record SendFriendRequestDto(string Username);
public sealed record DirectMessageDto(long Id, Guid From, Guid To, string Body, DateTimeOffset SentAt, DateTimeOffset? ReadAt);
public sealed record SendDirectMessageDto(Guid ToUserId, string Body);

public sealed record CreateChallengeDto(TimeControl TimeControl /* not Ranked */, Guid? ToUserId);
public sealed record ChallengeDto(string Code, string Url, PlayerCardDto From, Guid? ToUserId, TimeControl TimeControl,
    RuleSetDto Rules, ChallengeStatus Status, DateTimeOffset ExpiresAt, Guid? MatchId);
public sealed record AcceptChallengeResultDto(bool Accepted, string? Error, Guid? MatchId);

public sealed record RewardClaimDto(RewardKind Kind, Guid? GameId, string Receipt);
public sealed record RewardClaimResultDto(bool Granted, string? Error, ProgressDto? Progress);

public sealed record BotKeyDto(string KeyId, Guid BotUserId, string BotName, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
public sealed record CreateBotKeyDto(string BotName);
public sealed record CreatedBotKeyDto(string KeyId, string ApiKey /* "tttb_{keyId}_{secret}", shown once */, Guid BotUserId);

public sealed record NotificationDto(NotificationKind Kind, string Title, string Body, Guid? MatchId, string? Link);
```

### 5.6 Hub interfaces (verbatim)
```csharp
namespace TicTacToeUltimate.Contracts;

/// <summary>Client -> server. Hub at HubRoutes.Game. Method names are the C# names (SignalR default).</summary>
public interface IGameHub
{
    // session (WP2)
    Task<ServerHelloDto> Hello(ClientHelloDto hello);
    Task Pong(int pingId);
    Task<long> ServerTime();                                   // server epoch ms; used by ClockSync
    // queue (WP2)
    Task<QueueTicketDto> EnterQueue(QueueRequestDto request);
    Task LeaveQueue();
    // match (WP2)
    Task<MatchSnapshotDto> JoinMatch(Guid matchId);            // participant, or spectator if allowed; throws HubException(code) otherwise
    Task LeaveMatch(Guid matchId);                             // detach this connection only; clocks keep running
    Task<MoveAckDto> SubmitAction(SubmitActionDto request);
    Task<CommandAckDto> Resign(Guid matchId);
    Task<CommandAckDto> SendEmote(Guid matchId, string emoteId);
    Task<CommandAckDto> OfferRematch(Guid matchId);
    // challenges (WP2)
    Task<ChallengeDto> CreateChallenge(CreateChallengeDto request);
    Task<AcceptChallengeResultDto> AcceptChallenge(string code);
    Task<CommandAckDto> DeclineChallenge(string code);
    Task<CommandAckDto> CancelChallenge(string code);
    // social (WP3)
    Task<DirectMessageDto> SendDirectMessage(SendDirectMessageDto request);
    Task MarkConversationRead(Guid friendId, long upToMessageId);
}

/// <summary>Server -> client.</summary>
public interface IGameClient
{
    Task Ping(int pingId);
    Task QueueStatus(QueueStatusDto status);
    Task MatchFound(MatchFoundDto found);
    Task MatchEvent(MatchEventEnvelope envelope);              // strictly increasing Seq per match
    Task PostGameSummary(PostGameSummaryDto summary);
    Task ChallengeReceived(ChallengeDto challenge);
    Task ChallengeUpdated(ChallengeDto challenge);
    Task PresenceChanged(PresenceDto presence);
    Task FriendRequestReceived(FriendRequestDto request);
    Task FriendsChanged();
    Task DirectMessage(DirectMessageDto message);
    Task Notification(NotificationDto notification);
}
```
Expected rejections are returned in `MoveAckDto`/`CommandAckDto`/`QueueTicketDto`/`AcceptChallengeResultDto` with an `ErrorCodes` value. Only protocol misuse or authorization failures throw `HubException(code)`; the message is the error code string.

### 5.7 Bot WebSocket messages (verbatim; `TicTacToeUltimate.Contracts.Bot`)
```csharp
namespace TicTacToeUltimate.Contracts.Bot;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BotHello), "hello")] [JsonDerivedType(typeof(BotGameStart), "gameStart")]
[JsonDerivedType(typeof(BotState), "state")] [JsonDerivedType(typeof(BotGameEnd), "gameEnd")]
[JsonDerivedType(typeof(BotAck), "ack")] [JsonDerivedType(typeof(BotError), "error")] [JsonDerivedType(typeof(BotPing), "ping")]
public abstract record BotServerMessage;
public sealed record BotHello(int Protocol, Guid BotId, string BotName, long ServerTimeMs) : BotServerMessage;
public sealed record BotGameStart(Guid MatchId, MatchMode Mode, TimeControl TimeControl, RuleSetDto Rules, int Seat, Color You,
    PlayerCardDto Opponent) : BotServerMessage;
public sealed record BotState(Guid MatchId, long Seq, int Ply, GamePhase Phase, Color ToMove, Color You, bool YourTurn,
    string Board, string Actions /* Notation */, int? NextToVanish, int[] Legal, bool Swapped,
    long? DeadlineAtMs, int? AllowanceMs, long ServerTimeMs) : BotServerMessage;
public sealed record BotGameEnd(Guid MatchId, Outcome Outcome, EndReason Reason, int? WinnerSeat, int[] WinLine, bool YouWon) : BotServerMessage;
public sealed record BotAck(string Id, Guid? MatchId, long? Seq) : BotServerMessage;
public sealed record BotError(string? Id, Guid? MatchId, string Code, string Message) : BotServerMessage;
public sealed record BotPing(long T) : BotServerMessage;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BotSeek), "seek")] [JsonDerivedType(typeof(BotUnseek), "unseek")]
[JsonDerivedType(typeof(BotAcceptChallenge), "acceptChallenge")] [JsonDerivedType(typeof(BotAction), "action")]
[JsonDerivedType(typeof(BotResign), "resign")] [JsonDerivedType(typeof(BotGetState), "getState")] [JsonDerivedType(typeof(BotPong), "pong")]
public abstract record BotClientMessage;
public sealed record BotSeek(string? Id, TimeControl[] TimeControls, int MaxConcurrentGames) : BotClientMessage;
public sealed record BotUnseek(string? Id) : BotClientMessage;
public sealed record BotAcceptChallenge(string Id, string Code) : BotClientMessage;
public sealed record BotAction(string Id, Guid MatchId, int Ply, ActionKind Kind, int? Cell) : BotClientMessage;
public sealed record BotResign(string? Id, Guid MatchId) : BotClientMessage;
public sealed record BotGetState(string? Id, Guid MatchId) : BotClientMessage;
public sealed record BotPong(long T) : BotClientMessage;
```

### 5.8 JSON source-generation context (verbatim header; WP0 lists every DTO above plus arrays)
```csharp
namespace TicTacToeUltimate.Contracts;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(MatchEventEnvelope))]
[JsonSerializable(typeof(MatchSnapshotDto))]
[JsonSerializable(typeof(BotServerMessage))]
[JsonSerializable(typeof(BotClientMessage))]
// ... one [JsonSerializable] per DTO in §5.1-5.5, plus T[] for every array returned by REST, plus long, int, Guid, string
public sealed partial class ContractsJsonContext : JsonSerializerContext;
```
Server SignalR, server REST (`ConfigureHttpJsonOptions`), the client `HttpClient` and SignalR client, and the bot WS all insert `ContractsJsonContext.Default` at position 0 of `TypeInfoResolverChain`.

---

## 6. Server (`/TicTacToeUltimate.Server`)

### 6.1 Folders and owners
```
TicTacToeUltimate.Server/
  TicTacToeUltimate.Server.csproj          WP0 (frozen; refs Contracts; packages per §3.3)
  Program.cs                               WP0 (frozen, verbatim §6.2)
  appsettings.json, appsettings.Development.json, Properties/launchSettings.json   WP0 (frozen, §6.10)
  Hosting/**                               WP0: TttOptions, AddTttCore/UseTttCore/MapTttCore, CORS, ProblemDetails,
                                           UserNotifier, LoggingPushSender, Null* fallbacks, ClaimsPrincipal helpers
  Abstractions/**                          WP0 (frozen, verbatim §6.3)
  Realtime/GameHub.cs                      WP0 (root partial, verbatim §6.4)
  Realtime/GameHub.{Session,Queue,Match,Challenge}.cs   WP2
  Realtime/GameHub.Social.cs               WP3
  Analysis/**                              WP1 (TablebaseCache, AnalysisService, AnalysisServiceCollectionExtensions)
  Matches/**  Matchmaking/**  Challenges/**  Presence/**   WP2 (incl. MatchesServiceCollectionExtensions, MatchEndpoints)
  Auth/** Persistence/** Ratings/** Progression/** Social/** Rewards/** Api/**   WP3 (incl. PlatformServiceCollectionExtensions)
  Bots/**                                  WP4 (incl. BotsServiceCollectionExtensions, BotKeyEndpoints)
```

### 6.2 Program.cs (verbatim)
```csharp
using TicTacToeUltimate.Server.Analysis;
using TicTacToeUltimate.Server.Bots;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Platform;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddTttPlatform(cfg, builder.Environment);   // WP3: auth, persistence, ratings, progression, social, rewards, REST
builder.Services.AddTttAnalysis(cfg);                        // WP1: tablebase cache, analysis
builder.Services.AddTttMatches(cfg);                         // WP2: actors, registry, matchmaking, challenges, presence
builder.Services.AddTttBots(cfg);                            // WP4: bot gateway
builder.Services.AddTttCore(cfg, builder.Environment);       // WP0: MUST BE LAST (options, TimeProvider, SignalR, CORS, TryAdd null fallbacks)

var app = builder.Build();
app.UseTttCore();                 // WP0: exception handler/ProblemDetails, CORS, WebSockets
app.UseAuthentication();
app.UseAuthorization();
app.MapTttPlatform();             // WP3
app.MapTttMatches();              // WP2
app.MapTttBots();                 // WP4
app.MapTttCore();                 // WP0: hub at HubRoutes.Game, GET /health, OpenAPI in Development
app.Run();

public partial class Program;
```
Each `AddTttX`/`MapTttX` is a static extension in the owner's folder. Namespaces: `TicTacToeUltimate.Server.Platform` (file `Api/PlatformServiceCollectionExtensions.cs`), `.Analysis`, `.Matches`, `.Bots`, `.Hosting`. WP0 creates each as a compiling stub (an empty body returning `services`/`app`). Owners fill them in. Owners register real implementations with `AddSingleton`; `AddTttCore` uses `TryAdd*` for the null fallbacks, so real implementations win automatically.

`AddTttCore` does the following (WP0):
- Binds `TttOptions` from section `Ttt` with `ValidateOnStart`.
- `services.TryAddSingleton(TimeProvider.System)`.
- `AddSignalR(o => { o.MaximumReceiveMessageSize = 16 * 1024; o.EnableDetailedErrors = env.IsDevelopment(); })` with `.AddJsonProtocol(...)` inserting `ContractsJsonContext.Default`.
- `IUserIdProvider` = `SubUserIdProvider` (returns the `sub` claim).
- `AddAuthentication()` and `AddAuthorization()` only if WP3 has not, using `TryAdd` semantics. Policies are registered by WP3.
- CORS policy `"web"`: `WithOrigins(Ttt:Cors:Origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()`.
- `AddProblemDetails`.
- `ConfigureHttpJsonOptions` with the contracts context.
- `AddHealthChecks`.
- TryAdd fallbacks: `NullMatchResultSink`, `NullPresenceSink`, `NullBotPool`, `NullTablebaseCache`, `NullAnalysisService`, `LoggingPushSender`, `DefaultPlayerCardService` (returns `DisplayName = "Player"` cards), and the real `UserNotifier`.

### 6.3 Server abstractions (verbatim; `TicTacToeUltimate.Server.Abstractions`; WP0; frozen)
```csharp
namespace TicTacToeUltimate.Server.Abstractions;

// ---------- identity ----------
public static class TttClaims { public const string Kind = "ttt_kind"; public const string Name = "name"; public const string Sub = "sub"; }
public static class TttPolicies { public const string Account = "Account"; public const string Bot = "Bot"; }
public sealed record TttUser(Guid UserId, AccountKind Kind, string DisplayName)
{
    public bool IsGuest => Kind == AccountKind.Guest;
    public bool IsBot => Kind == AccountKind.Bot;
}
public static class ClaimsPrincipalExtensions
{
    /// <summary>Reads sub/ttt_kind/name. Throws HubException/UnauthorizedAccessException if missing.</summary>
    public static TttUser ToTttUser(this System.Security.Claims.ClaimsPrincipal principal);
}

// ---------- match setup / results ----------
public enum MatchOrigin { Queue, Challenge, Rematch, BotFallback }
public sealed record SeatSetup(Guid UserId, string DisplayName, AccountKind Kind);
public sealed record MatchSetup(
    Guid MatchId, MatchMode Mode, TimeControl TimeControl, RuleSet Rules, bool Rated,
    SeatSetup SeatA /* starts as X */, SeatSetup SeatB, ulong Seed, MatchOrigin Origin,
    Guid? RematchOf, string? ChallengeCode);
public sealed record StoredAction(int Ply, int Seat, GameAction Action, int ThinkMs, bool Auto, int? VanishedCell, DateTimeOffset At);
public sealed record CompletedMatch(MatchSetup Setup, bool Swapped, IReadOnlyList<StoredAction> Actions,
    MatchResultDto Result, DateTimeOffset StartedAt, DateTimeOffset EndedAt);

/// <summary>Implemented by WP3 (PostGameProcessor). Called exactly once per ended match (incl. Aborted). Must not block: enqueue and return.</summary>
public interface IMatchResultSink { ValueTask EnqueueAsync(CompletedMatch match, CancellationToken ct); }

// ---------- match gateway (WP2 implements; WP4 bots and tests consume) ----------
public interface IMatchObserver
{
    ValueTask OnEventAsync(MatchEventEnvelope envelope, CancellationToken ct);
}
public sealed record AttachResult(MatchSnapshotDto Snapshot, IAsyncDisposable Subscription);   // disposing = detach (seat shows disconnected)

public interface IMatchGateway
{
    Task<Guid> CreateMatchAsync(MatchSetup setup, CancellationToken ct);
    /// <summary>Attach a non-SignalR participant (bot, test). Throws MatchAccessException(ErrorCodes.*) if not a participant.</summary>
    Task<AttachResult> AttachAsync(Guid matchId, Guid userId, IMatchObserver observer, CancellationToken ct);
    Task<MatchSnapshotDto?> GetSnapshotAsync(Guid matchId, Guid? viewerUserId, CancellationToken ct);
    Task<MoveAckDto> SubmitActionAsync(Guid matchId, Guid userId, int expectedPly, GameAction action, Guid actionId, CancellationToken ct);
    Task<CommandAckDto> ResignAsync(Guid matchId, Guid userId, CancellationToken ct);
    IReadOnlyList<Guid> ActiveMatchesFor(Guid userId);
}
public sealed class MatchAccessException(string code) : Exception(code) { public string Code { get; } = code; }

// ---------- challenges (WP2 implements; WP4 consumes) ----------
public interface IChallengeService
{
    Task<ChallengeDto> CreateAsync(TttUser creator, CreateChallengeDto request, CancellationToken ct);
    Task<ChallengeDto?> GetAsync(string code, CancellationToken ct);
    Task<AcceptChallengeResultDto> AcceptAsync(TttUser user, string code, CancellationToken ct);
    Task<CommandAckDto> DeclineAsync(TttUser user, string code, CancellationToken ct);
    Task<CommandAckDto> CancelAsync(TttUser user, string code, CancellationToken ct);
}

// ---------- presence (WP2 implements tracker; WP3 implements sink) ----------
public interface IPresenceTracker
{
    PresenceDto Get(Guid userId);
    bool IsOnline(Guid userId);
}
/// <summary>WP3 fans out to friends. Called from WP2 after debounce (3 s for offline). Must not block.</summary>
public interface IPresenceSink { ValueTask OnPresenceChangedAsync(PresenceDto presence, CancellationToken ct); }

// ---------- player cards (WP3 implements; WP2 consumes for seats/MatchFound) ----------
public interface IPlayerCardService
{
    Task<PlayerCardDto> GetCardAsync(Guid userId, RatingPool pool, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, PlayerCardDto>> GetCardsAsync(IReadOnlyCollection<Guid> userIds, RatingPool pool, CancellationToken ct);
}

// ---------- bots (WP4 implements; WP2 matchmaking consumes) ----------
public sealed record BotReservation(Guid BotUserId, string BotName);
public interface IBotPool
{
    /// <summary>Atomically reserves one game slot on a connected bot that seeks this time control.</summary>
    bool TryReserve(TimeControl timeControl, int rating, [NotNullWhen(true)] out BotReservation? reservation);
    void Release(Guid botUserId);
    /// <summary>Called by WP2 after CreateMatchAsync; the bot session then attaches via IMatchGateway.</summary>
    ValueTask NotifyMatchCreatedAsync(Guid botUserId, Guid matchId, CancellationToken ct);
}

// ---------- analysis (WP1 implements; WP3 consumes) ----------
public enum TablebaseStatus { Unavailable, Warming, Ready }
public interface ITablebaseCache
{
    TablebaseStatus GetStatus(RuleSet rules);
    bool TryGet(RuleSet rules, [NotNullWhen(true)] out Tablebase? tablebase);   // never blocks; may trigger lazy build
}
public interface IAnalysisService
{
    /// <summary>null when no tablebase for the ruleset (status says why).</summary>
    Task<GameAnalysisDto?> AnalyzeAsync(GameRecord game, CancellationToken ct);
}

// ---------- delivery (WP0 implements) ----------
public interface IUserNotifier
{
    Task ToUserAsync(Guid userId, Func<IGameClient, Task> send);
    Task ToUsersAsync(IEnumerable<Guid> userIds, Func<IGameClient, Task> send);
}
public interface IPushSender { Task SendAsync(Guid userId, NotificationDto notification, CancellationToken ct); }  // stub logs
```

Persistence abstractions (`TicTacToeUltimate.Server.Abstractions.Persistence`; verbatim):
```csharp
namespace TicTacToeUltimate.Server.Abstractions.Persistence;

public sealed record UserRecord(Guid Id, AccountKind Kind, string? Username, string DisplayName, string AvatarId,
    string? Country, string TimeZone, int RenamesLeft, DateTimeOffset? TosAcceptedAt, string? DeviceId,
    DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);
public sealed record ProfilePatch(string? AvatarId, string? Country, string? TimeZone);
public enum UsernameResult { Ok, Taken, Invalid, NoRenamesLeft }

public interface IUserRepository
{
    Task<UserRecord?> GetAsync(Guid id, CancellationToken ct);
    Task<UserRecord?> GetByUsernameAsync(string username, CancellationToken ct);     // case-insensitive
    Task<UserRecord?> GetByDeviceIdAsync(string deviceId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, UserRecord>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<bool> CreateAsync(UserRecord user, CancellationToken ct);                    // false if id or username exists
    /// <summary>First set (Guest->Account upgrade or Account onboarding) does not consume a rename; later sets do.</summary>
    Task<UsernameResult> SetUsernameAsync(Guid id, string username, DateTimeOffset? tosAcceptedAt, CancellationToken ct);
    Task UpdateProfileAsync(Guid id, ProfilePatch patch, CancellationToken ct);
    Task TouchAsync(Guid id, DateTimeOffset lastSeenAt, CancellationToken ct);
    Task<IReadOnlyList<UserRecord>> SearchAsync(string prefix, int take, CancellationToken ct);
    Task<IReadOnlyList<UserRecord>> ListAsync(int take, CancellationToken ct);        // dev user picker
}

public sealed record RatingRecord(Guid UserId, RatingPool Pool, double Rating, double Rd, double Volatility,
    int Games, int Wins, int Losses, int Draws, double Peak, DateTimeOffset? LastGameAt)
{
    public static RatingRecord Initial(Guid userId, RatingPool pool) => new(userId, pool, 1200, 350, 0.06, 0, 0, 0, 0, 1200, null);
}
public sealed record LeaderboardQuery(RatingPool Pool, LeaderboardScope Scope, string? Country, Guid Viewer,
    IReadOnlyCollection<Guid>? RestrictTo /* friends scope */, int MinGames, double? MinRating /* master */, int Page, int PageSize);
public sealed record LeaderboardEntry(int Rank, Guid UserId, double Rating, int Games);
public sealed record LeaderboardPage(IReadOnlyList<LeaderboardEntry> Rows, int TotalCount, LeaderboardEntry? Viewer);

public interface IRatingRepository
{
    Task<RatingRecord> GetAsync(Guid userId, RatingPool pool, CancellationToken ct);  // Initial(...) if missing
    Task<IReadOnlyList<RatingRecord>> GetAllAsync(Guid userId, CancellationToken ct);
    Task PutAsync(RatingRecord record, CancellationToken ct);                          // single writer: PostGameProcessor
    Task<LeaderboardPage> QueryAsync(LeaderboardQuery query, CancellationToken ct);
}

public sealed record RatingSnapshot(int Seat, RatingPool Pool, double Before, double After);
public sealed record GameRecord(Guid Id, MatchSetup Setup, bool Swapped, IReadOnlyList<StoredAction> Actions,
    MatchResultDto Result, DateTimeOffset StartedAt, DateTimeOffset EndedAt, IReadOnlyList<RatingSnapshot> Ratings);
public sealed record GameListItem(Guid Id, MatchSetup Setup, bool Swapped, MatchResultDto Result, int Plies,
    DateTimeOffset EndedAt, IReadOnlyList<RatingSnapshot> Ratings);

public interface IGameRepository
{
    Task<bool> SaveCompletedAsync(GameRecord game, CancellationToken ct);              // false if already saved (idempotency)
    Task<GameRecord?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<GameListItem>> ListForUserAsync(Guid userId, DateTimeOffset? before, int take, CancellationToken ct);
    Task<int> CountForUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>Durable live state for Daily matches only.</summary>
public sealed record StoredDailyMatch(MatchSetup Setup, IReadOnlyList<StoredAction> Actions, long Version,
    DateTimeOffset? TurnDeadline, bool Ended, DateTimeOffset StartedAt);
public sealed class ConcurrencyException(Guid matchId) : Exception($"Version conflict on {matchId}");
public interface IDailyMatchRepository
{
    Task CreateAsync(MatchSetup setup, DateTimeOffset startedAt, DateTimeOffset firstDeadline, CancellationToken ct);
    /// <summary>Returns new version; throws ConcurrencyException if expectedVersion mismatches.</summary>
    Task<long> AppendActionAsync(Guid matchId, long expectedVersion, StoredAction action, DateTimeOffset? nextDeadline, CancellationToken ct);
    Task MarkEndedAsync(Guid matchId, CancellationToken ct);
    Task<StoredDailyMatch?> LoadAsync(Guid matchId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<StoredDailyMatch>> ListActiveForUserAsync(Guid userId, CancellationToken ct);
}

public enum FriendRequestResult { Created, AutoAccepted, AlreadyFriends, AlreadyRequested, Blocked, NotFound }
public sealed record FriendRequestRecord(Guid Id, Guid From, Guid To, DateTimeOffset CreatedAt);
public interface IFriendRepository
{
    Task<FriendRequestResult> RequestAsync(Guid from, Guid to, DateTimeOffset at, CancellationToken ct);   // mutual pending -> auto-accept
    Task<bool> RespondAsync(Guid requestId, Guid responder, bool accept, CancellationToken ct);
    Task RemoveAsync(Guid a, Guid b, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListFriendsAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<FriendRequestRecord>> ListRequestsAsync(Guid userId, CancellationToken ct);       // incoming + outgoing
    Task<bool> AreFriendsAsync(Guid a, Guid b, CancellationToken ct);
    Task BlockAsync(Guid blocker, Guid blocked, CancellationToken ct);                                  // also removes friendship/requests
    Task UnblockAsync(Guid blocker, Guid blocked, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListBlockedAsync(Guid blocker, CancellationToken ct);
    Task<bool> IsBlockedEitherWayAsync(Guid a, Guid b, CancellationToken ct);
}

public sealed record DirectMessageRecord(long Id, Guid From, Guid To, string Body, DateTimeOffset SentAt, DateTimeOffset? ReadAt);
public interface IChatRepository
{
    Task<DirectMessageRecord> AppendAsync(Guid from, Guid to, string body, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<DirectMessageRecord>> PageAsync(Guid a, Guid b, long? beforeId, int take, CancellationToken ct); // newest first
    Task MarkReadAsync(Guid reader, Guid other, long upToId, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> UnreadCountsAsync(Guid reader, CancellationToken ct);
}

public sealed record StreakRecord(Guid UserId, int Current, int Best, DateOnly? LastPlayedDay, int Freezes);
public sealed record QuestDayRecord(Guid UserId, DateOnly Day, int Wins, bool FreezeAwarded, int? BonusTarget /* wins target of bonus quest */, bool BonusClaimed);
public interface IProgressRepository
{
    Task<StreakRecord> GetStreakAsync(Guid userId, CancellationToken ct);
    Task PutStreakAsync(StreakRecord record, CancellationToken ct);
    Task<QuestDayRecord> GetQuestDayAsync(Guid userId, DateOnly day, CancellationToken ct);
    Task PutQuestDayAsync(QuestDayRecord record, CancellationToken ct);
}

public sealed record ChallengeRecord(string Code, Guid CreatorId, Guid? TargetUserId, TimeControl TimeControl, string RulesId,
    ChallengeStatus Status, Guid? MatchId, Guid? AcceptedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public interface IChallengeRepository
{
    Task<bool> CreateAsync(ChallengeRecord record, CancellationToken ct);           // false on code collision
    Task<ChallengeRecord?> GetAsync(string code, CancellationToken ct);
    /// <summary>Compare-and-set on Status. Returns false if current status != expected.</summary>
    Task<bool> TryTransitionAsync(string code, ChallengeStatus expected, ChallengeStatus next, Guid? acceptedBy, Guid? matchId, CancellationToken ct);
    Task<IReadOnlyList<ChallengeRecord>> ListOpenForTargetAsync(Guid targetUserId, DateTimeOffset now, CancellationToken ct);
}

public sealed record BotKeyRecord(string KeyId, Guid BotUserId, Guid OwnerUserId, string BotName, byte[] SecretHash,
    DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
public interface IBotKeyRepository
{
    Task<BotKeyRecord?> FindAsync(string keyId, CancellationToken ct);
    Task CreateAsync(BotKeyRecord record, CancellationToken ct);
    Task<bool> RevokeAsync(string keyId, Guid ownerUserId, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<BotKeyRecord>> ListForOwnerAsync(Guid ownerUserId, CancellationToken ct);
}

public sealed record RewardGrantRecord(string Nonce, Guid UserId, RewardKind Kind, Guid? GameId, DateTimeOffset GrantedAt);
public interface IRewardLedger
{
    Task<bool> TryGrantAsync(RewardGrantRecord grant, CancellationToken ct);           // false if nonce already used
    Task<bool> HasGrantAsync(Guid userId, RewardKind kind, Guid? gameId, CancellationToken ct);
}

public sealed record SeasonRecord(int Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record MasterSeasonResult(int SeasonId, Guid UserId, double FinalRating, int Rank);
public interface ISeasonRepository
{
    Task<SeasonRecord> GetCurrentAsync(DateTimeOffset now, CancellationToken ct);      // creates first season if none
    Task CloseAsync(SeasonRecord season, IReadOnlyList<MasterSeasonResult> results, SeasonRecord next, CancellationToken ct);
}
```
All InMemory repositories are thread-safe singletons. They store immutable records, use a per-aggregate lock for read-modify-write, and do case-insensitive username uniqueness through a lowercase key.

### 6.4 GameHub root (verbatim, WP0)
```csharp
namespace TicTacToeUltimate.Server.Realtime;

[Authorize]
public sealed partial class GameHub(IServiceProvider services, ILogger<GameHub> logger) : Hub<IGameClient>, IGameHub
{
    private TttUser Me => Context.User!.ToTttUser();

    public override async Task OnConnectedAsync()
    {
        await OnConnectedSessionAsync();     // WP2, GameHub.Session.cs (presence, pinger registration)
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await OnDisconnectedSessionAsync(exception);   // WP2, GameHub.Session.cs (detach from matches, presence)
        await base.OnDisconnectedAsync(exception);
    }

    private partial Task OnConnectedSessionAsync();
    private partial Task OnDisconnectedSessionAsync(Exception? exception);
}
```
Every `IGameHub` method is implemented in the owner's partial file (§5.6 comments). Hub methods resolve services from `services`, so each partial can depend on its own services without changing the constructor. Hub methods never hold locks and never await anything except actor asks (2 s timeout) and repository I/O.

### 6.5 Match runtime (WP2): concurrency model

**One match = one actor.** `MatchActor` owns all match state and processes commands from a single-reader `Channel<MatchCommand>` (unbounded). There are no locks on match state. `Handle(cmd)` is **synchronous**: pure engine calls plus clock math. Side effects go to an outbox.

**Ingress is stamped and enqueued atomically.** This fixes the Tick/move race in the original proposal.
```csharp
private readonly Lock _ingress = new();
internal bool TryPost(Func<long, MatchCommand> create)
{
    lock (_ingress)
    {
        if (_closed) return false;
        return _inbox.Writer.TryWrite(create(_time.GetTimestamp()));   // stamp + enqueue under one lock
    }
}
```
Hub methods, bot sessions, timers and sweepers **all** post through `TryPost`. Therefore channel order equals stamp order. A move stamped at or before the deadline is always dequeued before the `Tick` the timer posted at the deadline.

Time spent before `TryPost` (transport, deserialization) counts against the player; the lag credit covers it. Timers use `TimeProvider.CreateTimer`. If a `Tick` arrives with `At < effectiveDeadline` (early timer), the handler re-arms for the remainder.

Commands (internal to WP2; shapes are a recommendation): `Attach, Detach, Submit(userId, expectedPly, action, actionId), Resign, Emote, OfferRematch, RttSample(userId, rttMs), Tick(turnToken), GetSnapshot(viewer), Shutdown`. Asks use a `TaskCompletionSource` (RunContinuationsAsynchronously) with a 2 s timeout. On timeout the caller gets `ErrorCodes.Internal`; the command may still apply, and `ActionId` dedupe makes a retry safe.

**Turn handling.**
- `TurnStart(now)`:
  - `turnToken++`.
  - `allowance = MoveClock.Allowance(tc, gameElapsed)`.
  - `credit[toMoveSeat] = min(halfRtt(seat), cap, budgetLeft(seat))`, frozen for this turn.
  - Arm the timer at `turnStart + allowance + credit`.
  - Emit `TurnStarted`.
- `Submit`:
  - Validate the participant, the seat to move, `ExpectedPly == state.Ply` (else `stale_ply`), and `ActionId` (a duplicate returns the original ack).
  - `rawThink = At − turnStart`.
  - If `rawThink > allowance + credit`, apply the timeout path and reject with `timeout`.
  - Otherwise `Rules.Apply`; an engine error maps through `ErrorCodes.FromMoveError`.
  - On success: `ThinkMs = max(0, rawThink − credit)`, `budgetLeft −= credit`, `gameElapsed += ThinkMs`. Emit `ActionApplied`, then either `MatchEnded` or the next `TurnStarted`.
- Timeout path:
  - Ply 0: `Aborted`.
  - Ranked/Daily: loss (`Timeout`).
  - Casual: auto action (D10) with `ThinkMs = allowance`, `Auto = true`, then continue.
  - Unlimited: no timer.
- Resign at ply 0 is `Aborted`; otherwise it is a loss.
- Emotes: sender must be a seat; id in `EmoteCatalog`; token bucket 1 per 2 s, burst 3; max 30 per seat per match.

**Events and ordering.**
- Every emitted event gets `Seq = ++seq` and is written to `_outbox` (a single-reader channel). `MatchPublisher` drains it in order. For each item it: (1) runs group adds for new attachments, (2) `Clients.Group("m:" + id).MatchEvent(envelope)`, (3) observer callbacks, (4) the result sink when the match ended. A slow client cannot stall clocks.
- **Join ordering proof.** `Attach` is handled inside the actor:
  1. Record the attachment.
  2. Build the snapshot at `Seq = S`.
  3. Enqueue `AddToGroup(conn)` (or the observer registration) to the outbox **before** returning.
  4. Return the snapshot.

  Every later event has seq > S and is published after the group add. Clients drop envelopes with `Seq <= lastSeq` and resync by calling `JoinMatch` again on a gap.

**Seats and colours.** Seat A starts as X. Colour is `Swapped ? flip : identity` (`SeatColors` helper). Identity always comes from the token (`Context.User`), never from the client.

**Connections.** `PresenceTracker` maps `userId → set<connectionId>`. A user may be attached from several tabs or devices; any of them may act, and `ActionId` plus `ExpectedPly` dedupe. A seat is `Connected` when at least one attachment (connection or observer) exists. Disconnect never pauses clocks.

**RTT.** The `RttPinger` (`BackgroundService`) sends `Ping(pingId)` every 2 s to users with a live match and every 15 s otherwise. `Pong(pingId)` gives RTT from server timestamps. Per user, keep the last 5 samples; `halfRtt = min(samples)/2`, clamped to [0, 1000]. The pinger posts `RttSample` to that user's live match actors. The seat card shows the latest RTT.

**Registry and lifecycle.**
- `MatchRegistry` holds a `ConcurrentDictionary<Guid, Lazy<Task<MatchActor>>>` and exposes `GetOrActivateAsync(id)` and `Create(setup)`. Activation is single-flight per id.
- A Daily actor rehydrates from `IDailyMatchRepository.LoadAsync` + `Rules.Replay`.
- After the match ends, the actor lingers 120 s (rematch window, late joins). It then closes:
  1. Set `_closed` under the ingress lock once the inbox is empty.
  2. Complete the inbox.
  3. Remove itself with `TryRemove(KeyValuePair(id, lazy))`.

  If a post to a closed actor returns false, the gateway re-activates and retries, up to 3 times.
- Daily actors passivate the same way after 10 min idle while live.
- On host shutdown, live non-Daily matches end `Aborted` with detail `"server_restart"` and are sent to the result sink.
- `UserActivityRegistry` (`ConcurrentDictionary`, `TryAdd`) enforces at most one queue entry **or** live non-Daily match per user (`already_in_queue` / `already_in_match`), plus at most 10 active Daily matches (`daily_limit`). Daily queue entries are tracked separately from live activity.

**Daily.**
- Daily uses the same actor with a 24 h allowance. Deadlines are persisted as wall-clock `DateTimeOffset`; lag credit does not apply.
- Before acknowledging, every action is persisted with `AppendActionAsync(expectedVersion)`. On `ConcurrencyException` (which only happens after an unexpected double activation), the actor reloads from the store, re-applies the command once, and logs an error.
- `DailyDeadlineSweeper` runs every 60 s: `ListDueAsync(now)`, then `GetOrActivateAsync`, then post `Tick`.
- When it becomes a user's turn, send `Notification(DailyYourTurn)` through `IUserNotifier` and `IPushSender`.

**Unlimited.** If the seat to move has had no attachment for `DisconnectForfeitMinutes`, that seat loses with `Abandoned`. The check runs on a 30 s actor timer.

**Spectating.**
- `JoinMatch` by a non-participant succeeds only for an account that is a friend of either seat. The snapshot has `YourSeat = null`. Spectators join the same group.
- `SpectatorCountChanged` is throttled to 1/s.
- `GET /api/live?friends=true` lists friends' live matches.

**Rematch.** Only when `!Rated`. Each seat offers within 60 s of the end; when both have offered, the actor creates the new match (seats swapped, `Origin = Rematch`) and emits `RematchStarted`.

### 6.6 Matchmaking, challenges (WP2)
- `Matchmaker` is a `BackgroundService` with a single loop and a `Channel<QueueCommand>` (Enter/Leave/Disconnect). It ticks every `TickMs` (250). Because it is single-threaded it cannot double-pair.
- Pools are keyed by `(TimeControl, PoolKind {Account, Guest})`. Ranked and Daily are Account only; guests get `guest_not_allowed`, and accounts without a username get `username_required`.
- Rating = the user's rating in `RatingPools.For(tc)`, hidden for casual pools.
- Window: `w = InitialWindow + WidenBy * floor(waitSeconds / WidenEverySeconds)`. A pair is valid iff `|r1 − r2| <= min(w1, w2)`.
- Scan order is oldest entry first. Among valid candidates, prefer the same region (`ClientHello.TimeZone` IANA prefix such as "Europe", "America"), then the smallest gap, then the longest wait.
- A guest entry waiting `GuestFallbackSeconds` (10) may pair with entries in the Account pool of the same casual time control (both windows still apply).
- An entry waiting `BotFallbackSeconds` (20) with `IBotPool.TryReserve` succeeding gets a bot match: `Origin = BotFallback`, never Ranked unless `Bots:Ranked`.
- On a pair:
  - `seed` is 8 bytes from `RandomNumberGenerator`.
  - Colours: `new SplitMix64(seed).Next() & 1` decides whether the first entry is seat A.
  - Create the match, send `MatchFound` to both users, and call `IBotPool.NotifyMatchCreatedAsync` for bot seats.
- Send `QueueStatus` every 1 s. Disconnecting all of a user's connections removes the entry after 5 s.
- Challenges:
  - The code is 8 chars of Crockford base32 from `RandomNumberGenerator`, with a 24 h TTL. The URL is `{Ttt:Web:BaseUrl}/c/{code}`. MAUI also handles `tttu://c/{code}`.
  - The creator must be an Account with a username. Allowed time controls are `CasualX2, CasualX4, Unlimited, Daily`. The match is `Mode = Challenge` and unrated (Daily challenges count toward the daily limit).
  - A targeted challenge requires friends and pushes `ChallengeReceived` to the target.
  - Accept is CAS `Open → Accepted`; the first acceptor wins. Guests may accept untargeted links. Accepting your own challenge returns `cannot_challenge_self`.
  - Expiry is lazy on read; a 5-minute sweep sends `ChallengeUpdated`.
- REST (WP2, `Matches/MatchEndpoints.cs`):
  - `GET /api/challenges/{code}` (anonymous) → `ChallengeDto`
  - `GET /api/daily` → `DailyGameDto[]`
  - `GET /api/live?friends=true` → `LiveMatchDto[]`

### 6.7 Platform (WP3)
**Auth.**
- Schemes:
  - `"Dev"`: JwtBearer, HS256, issuer `ttt-dev`, audience `ttt`. The key comes from `Ttt:Auth:Dev:SigningKey`; if empty, a random 32-byte key is generated at startup with a warning, and clients re-authenticate on 401.
  - `"Supabase"`: JwtBearer with authority `{Url}/auth/v1`, JWKS, audience `authenticated`, and an optional legacy HS256 `JwtSecret`. Used only if `Ttt:Auth:Supabase:Enabled`.
  - `"Ttt"`: a policy scheme, the default, that forwards by the unvalidated `iss` claim (`ttt-dev` → Dev, else Supabase).
  - `"BotKey"`: registered by WP4.
- For hub and bot paths, `OnMessageReceived` reads `access_token` from the query when the path starts with `/hubs`.
- Claims transformation for Supabase tokens: look up `UserRecord` by `sub`. If missing, create it with `Kind = is_anonymous ? Guest : Account` and `DisplayName = Guest#NNNN` or the email local part. Add `ttt_kind` and `name`.
- Policies: `Account` (ttt_kind == account) and `Bot` (ttt_kind == bot).
- Startup guard: `Dev.Enabled && env.IsProduction()` throws.
- Dev endpoints (only when Dev is enabled):
  - `GET /api/auth/dev/users` → `DevUserDto[]`
  - `POST /api/auth/dev/token` (`DevTokenRequestDto`; `NewUsername` creates an Account user) → `TokenResponseDto`
  - `POST /api/auth/guest` (`GuestTokenRequestDto`) creates or loads the guest by `DeviceId`, named `Guest#` + 4 digits → `TokenResponseDto`
- Dev tokens last 30 days. Guest upgrade: `PUT /api/me/username` on a guest sets `Kind = Account` and keeps the **same id**, so streak and history carry over.

**Usernames.** Normalize to lowercase; must match `^[a-z0-9_]{3,16}$`; must not be on the reserved list (`admin, administrator, mod, moderator, support, staff, system, root, ttt, tttu, guest, bot, null, undefined, anonymous, official`). The profanity list lives in `Social/Data/blocked-words.txt`, matched on substring after stripping `_` and digits. The first set requires `AcceptTos`; there is 1 free rename after that.

**Ratings.**
- `Glicko2` is pure: τ = 0.5, ε = 1e-6, one game = one rating period. Before an update, inflate RD for idle time by `φ' = sqrt(φ² + σ² · floor(idleDays / Ratings:IdlePeriodDays (7)))`, capped at 350. Provisional while `Games < 10`.
- `TierCalculator` is pure and reads `Ttt:Tiers`. `TierInfo` is shown only for visible pools.
- Master seasons: `SeasonService` (`BackgroundService`, hourly check). At season end it:
  1. Snapshots Ranked players with `Rating >= MasterMin` into results.
  2. Resets those players to `MasterMin` with RD 150.
  3. Starts the next season (`SeasonLengthDays` 90).

  Other tiers are untouched.

**PostGameProcessor : IMatchResultSink.** A single-consumer channel, so ratings are updated serially even across concurrent Daily matches. For each `CompletedMatch`:
1. Compute (do not persist yet) the rating updates. This applies only if the match is not Aborted and not a Challenge; Glicko scores are 1 / 0 / 0.5. For each human seat:
   - The visible pool, if `Rated`.
   - The hidden pool (CasualX2/CasualX4/Unlimited), if both seats are human.
2. `IGameRepository.SaveCompletedAsync` with the `RatingSnapshot`s. **If it returns false, stop**; this is the idempotency check, and nothing below runs twice.
3. `IRatingRepository.PutAsync` for each updated record. Compute tier before/after for visible pools; on a change, send `Notification(Promotion|Demotion)`.
4. Streak (non-aborted games only), using the user's local day `D` from `UserRecord.TimeZone`:
   - `Last == D`: no change.
   - `Last == D-1`: `Current+1`.
   - Gap `g = D-Last-1` with `g <= Freezes`: consume `g` freezes, then `Current+1`.
   - Otherwise: `Current = 1`.
   - `Best = max(Best, Current)`.
5. Quests:
   - A win increments `Wins`. The quests are win1, win3 and win5.
   - When all three complete for the first time today and `Freezes < 2`, add +1 freeze and set `FreezeAwarded`.
   - The bonus quest (ExtraQuest reward) completes at `Wins >= BonusTarget`, where `BonusTarget = winsAtGrant + 2`.
6. Analysis: if `ITablebaseCache` is Ready for the ruleset, compute the per-seat blunder counts.
7. Send `PostGameSummary` to each human seat through `IUserNotifier`.

Bots get no summary. `WinStreak` = consecutive wins in the same visible pool, computed from history.

**Social.**
- Friends: request by username (blocked either way → `blocked`; a mutual pending request auto-accepts), accept or decline, remove, block, unblock.
- `FriendsChanged` and `FriendRequestReceived` are pushed over the hub.
- `PresenceSink` fans `PresenceChanged` out to online friends.
- DMs (`GameHub.Social.cs`):
  - Only between mutual friends who are not blocked (`not_friends` / `blocked`).
  - Body 1..500 chars after trim; profanity is masked with `*`.
  - Rate limit 1/s, burst 5.
  - Delivered to the recipient's connections via `DirectMessage`.
- Retention: unlimited **[OPEN]**.

**Rewards.**
- `POST /api/rewards/claim`. The `IRewardVerifier` stub (`Ttt:Ads:Provider = "Stub"`) accepts receipts matching `^stub:[a-z]+:[0-9a-f-]{36}$`; the nonce is the receipt.
- Effects:
  - StreakFreeze: `+1`, or `reward_capped` at 2.
  - ExtraQuest: once per local day.
  - Analysis: requires `GameId` of a game the user played. It grants the entitlement checked by `GET /api/games/{id}/analysis`.

**REST (WP3; all under `[Authorize]` unless noted; errors as ProblemDetails with an extension `code` = `ErrorCodes`).**

| Method and path | Policy | Body → Response |
|---|---|---|
| GET `/api/config` | anonymous | → `AppConfigDto` |
| GET `/api/auth/dev/users`, POST `/api/auth/dev/token`, POST `/api/auth/guest` | anonymous, Dev only | see above |
| GET `/api/me` | any | → `MeDto` |
| PUT `/api/me/username` | any (guest = upgrade) | `SetUsernameDto` → `MeDto` |
| PATCH `/api/me` | any | `UpdateProfileDto` → `MeDto` |
| GET `/api/players/{username}` | any | → `ProfileDto` |
| GET `/api/players/search?q=` | Account | → `PlayerCardDto[]` (prefix, max 20) |
| GET `/api/players/{userId}/games?before=&take=` | any (guests: self only) | → `GameSummaryDto[]` |
| GET `/api/games/{id}` | any | → `GameRecordDto` |
| GET `/api/games/{id}/analysis` | any | → `GameAnalysisDto` (403 `reward_required`, 503 `analysis_warming`, 404 `analysis_unavailable`) |
| GET `/api/leaderboards/{pool}?scope=&country=&page=` | Account | → `LeaderboardPageDto` (Global/Country require `Games >= MinRatedGames` (20); MasterSeason = Ranked, rating ≥ Master) |
| GET `/api/friends` | Account | → `FriendsOverviewDto` |
| POST `/api/friends/requests` | Account | `SendFriendRequestDto` → `CommandAckDto` |
| POST `/api/friends/requests/{id}/accept`, `/decline` | Account | → `CommandAckDto` |
| DELETE `/api/friends/{userId}` | Account | → 204 |
| POST / DELETE `/api/blocks/{userId}` | Account | → 204 |
| GET `/api/chat/{friendId}?before=&take=` | Account | → `DirectMessageDto[]` |
| GET `/api/progress` | any | → `ProgressDto` |
| POST `/api/rewards/claim` | any | `RewardClaimDto` → `RewardClaimResultDto` |

**Fixtures** (`Persistence/InMemory/DevFixtures.cs`, deterministic ids `00000000-0000-0000-0000-0000000000NN`, seeded when `Persistence:SeedFixtures`):

| Username | Ranked rating (games) | Other |
|---|---|---|
| `ali_xo` (01) | 1552 (412) | CA, Daily 1610 |
| `kestrel_99` (02) | 1612 | |
| `mira_o` (03) | 2031 | Diamond III. The mockup's `mira.o` is not a valid username |
| `jules_x` (04) | 1342 | |
| `tic_tac_tess` (05) | 1180 | |
| `rook_raider` (06) | 1490 | |
| `pixel_pawn` (07) | 1405 | |
| `newbie_ned` (08) | provisional, 3 games | |

Relationships and progress:
- `ali_xo` is friends with 02, 03, 04, 05.
- 06 has a pending request to `ali_xo`.
- 06 has an open targeted CasualX2 challenge to `ali_xo`.
- About 12 finished games per fixture, generated by deterministic engine playouts.
- `ali_xo` has a streak of 12 (best 31) and today's quest wins = 2.

`supabase/seed.sql` mirrors the fixtures.

### 6.8 Analysis (WP1)
- `TablebaseCache : ITablebaseCache, IHostedService`. At startup it builds `Analysis:Precompute` on a background thread (`Task.Run`); startup is never blocked.
- Other rulesets are built lazily on first `TryGet` if `Tablebase.CanBuild` holds, within `Analysis:MaxStates` (5,000,000). At most one build runs at a time.
- The cache key is the ruleset with `Opening = PieSwap` normalized to `None`, because pie does not change the reachable placement positions (§4.4). `BanCenter` is **not** normalized, because it changes the reachable set.
- `AnalysisService` maps `GameRecord` → `GameAnalysisDto` through `GameAnalyzer`.

### 6.9 Bot gateway (WP4)
- Path `/bot/ws`.
- Auth: `Authorization: Bearer tttb_{keyId}_{secret}` (or `?key=` only if `Bots:AllowQueryKey`). Look up the key by `keyId` and compare `SHA256(secret)` in constant time.
- Dev keys come from `Bots:DevKeys` (`[{ "Name": "dev_bot", "Key": "tttb_devkey01_devsecret01" }]`); each creates a Bot user with a deterministic id at startup.
- `?protocol=1` is required. Close codes:
  - 4401: bad key
  - 4426: protocol mismatch
  - 4408: 3 missed pings (ping every 15 s)
- Concurrency:
  - One receive loop per socket.
  - All sends go through a per-socket `Channel<BotServerMessage>` drained by **one** writer task.
  - `BotSession : IMatchObserver` maps envelopes to `state`/`gameEnd`.
  - One socket multiplexes up to `maxConcurrentGames` matches.
- `BotPool : IBotPool` tracks seeking sessions and their free capacity.
- Bot actions go through `IMatchGateway.SubmitActionAsync` with `ActionId = GuidV5(botSessionId, message.Id)`, so retries are idempotent.
- Endpoints `GET/POST/DELETE /api/bots/keys` (Account) manage the owner's keys; a bot user is created per key.
- Full protocol text in `spec/bot-protocol.md` (§7.1).

### 6.10 Configuration (`TicTacToeUltimate.Server/appsettings.json`, verbatim; Development overrides only `Auth:Dev:Enabled=true`)
```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "Ttt": {
    "Auth": {
      "Dev": { "Enabled": false, "SigningKey": "", "TokenDays": 30 },
      "Supabase": { "Enabled": false, "Url": "", "AnonKey": "", "JwtSecret": "", "Audience": "authenticated" }
    },
    "Persistence": { "Provider": "InMemory", "SeedFixtures": true, "Postgres": { "ConnectionString": "" } },
    "Modes": {
      "Ranked":    { "Rules": "n3k3-cap3-v0-w1-pie",  "Rated": true,  "GuestsAllowed": false },
      "CasualX2":  { "Rules": "n3k3-cap3-v0-w1-none", "Rated": false, "GuestsAllowed": true },
      "CasualX4":  { "Rules": "n3k3-cap3-v0-w1-none", "Rated": false, "GuestsAllowed": true },
      "Unlimited": { "Rules": "n3k3-cap3-v0-w1-none", "Rated": false, "GuestsAllowed": true },
      "Daily":     { "Rules": "n3k3-cap3-v0-w1-pie",  "Rated": true,  "GuestsAllowed": false }
    },
    "Clock": { "LagCreditCapMs": 300, "LagBudgetMs": 4000, "RttSamples": 5, "PingLiveSeconds": 2, "PingIdleSeconds": 15 },
    "Unlimited": { "DisconnectForfeitMinutes": 10 },
    "Daily": { "MaxConcurrent": 10, "PassivateMinutes": 10, "SweepSeconds": 60 },
    "Matchmaking": { "TickMs": 250, "InitialWindow": 100, "WidenBy": 50, "WidenEverySeconds": 3,
                     "GuestFallbackSeconds": 10, "BotFallbackSeconds": 20, "DisconnectGraceSeconds": 5 },
    "Challenges": { "TtlHours": 24 },
    "Rematch": { "WindowSeconds": 60 },
    "Analysis": { "MaxStates": 5000000, "Precompute": [ "n3k3-cap3-v0-w1-none", "n3k3-fifo7-v1-w0-none" ] },
    "Bots": { "Enabled": true, "Ranked": false, "AllowQueryKey": false,
              "DevKeys": [ { "Name": "dev_bot", "Key": "tttb_devkey01_devsecret01" } ] },
    "Ratings": { "Tau": 0.5, "ProvisionalGames": 10, "IdlePeriodDays": 7, "MinRatedGames": 20 },
    "Tiers": { "Bronze": 0, "Silver": 1100, "Gold": 1400, "Platinum": 1700, "Diamond": 2000, "Master": 2300, "DivisionWidth": 100 },
    "Seasons": { "LengthDays": 90, "MasterResetRd": 150 },
    "Ads": { "Provider": "Stub", "InterstitialEveryGames": 3, "SkipAfterTwoLosses": true },
    "Cors": { "Origins": [ "http://localhost:5028", "https://localhost:7008" ] },
    "Web": { "BaseUrl": "http://localhost:5028" }
  }
}
```
`appsettings.Development.json`:
```json
{ "Ttt": { "Auth": { "Dev": { "Enabled": true } } } }
```
Launch profile: `http://0.0.0.0:5091` and `https://0.0.0.0:7086;http://0.0.0.0:5091` (already present). `TttOptions` mirrors this tree exactly (`Hosting/TttOptions.cs`, WP0). Pie positions use the same tablebase as the default ruleset (§4.4), which is why only the default id is precomputed.

---

## 7. Shared specs for Go and bots

### 7.1 Bot protocol v1 (WP4 writes `spec/bot-protocol.md` from this)
- Transport: WebSocket, UTF-8 JSON text frames, one message per frame, camelCase, enums as strings (`"CasualX2"`, `"AwaitingPlacement"`, `"X"`).
- Every message has `"type"`. Client requests may carry `"id"`, which the server echoes in `ack`/`error`.
- Sequence:
  1. Connect with key and `?protocol=1`.
  2. Server sends `hello`.
  3. Bot sends `seek`.
  4. Server sends `gameStart` then `state` for each match.
  5. Bot sends `action` when `yourTurn`.
  6. Server sends `ack` or `error`, then `state` after every applied action (both sides).
  7. Server sends `gameEnd`.
- `ply` in `action` is the expected ply; a stale value gives `error` code `stale_ply`.

```jsonc
// server -> bot
{"type":"hello","protocol":1,"botId":"…","botName":"dev_bot","serverTimeMs":1760500000000}
{"type":"gameStart","matchId":"…","mode":"Casual","timeControl":"CasualX2","rules":{"id":"n3k3-cap3-v0-w1-none","boardSize":3,"winLength":3,"removal":"PerPlayerCap","window":3,"vacatedCellPlayable":false,"vanishingPieceCanWin":true,"opening":"None"},"seat":1,"you":"O","opponent":{"userId":"…","displayName":"ali_xo","avatarId":"a1","kind":"Account","provisional":false}}
{"type":"state","matchId":"…","seq":9,"ply":7,"phase":"AwaitingPlacement","toMove":"O","you":"O","yourTurn":true,"board":"O.OOXXX.X","actions":"4,0,8,2,6,3,5","nextToVanish":4,"legal":[1,4,7],"swapped":false,"deadlineAtMs":1760500031000,"allowanceMs":30000,"serverTimeMs":1760500001000}
{"type":"ack","id":"m-17","matchId":"…","seq":10}
{"type":"error","id":"m-17","matchId":"…","code":"occupied","message":"Cell 4 is occupied"}
{"type":"gameEnd","matchId":"…","outcome":"OWins","reason":"Line","winnerSeat":1,"winLine":[0,1,2],"youWon":true}
{"type":"ping","t":123}
// bot -> server
{"type":"seek","id":"s1","timeControls":["CasualX2","Unlimited"],"maxConcurrentGames":4}
{"type":"unseek","id":"s2"}
{"type":"acceptChallenge","id":"c1","code":"K7Q2XM4P"}
{"type":"action","id":"m-17","matchId":"…","ply":7,"kind":"Place","cell":1}
{"type":"action","id":"m-18","matchId":"…","ply":1,"kind":"Swap"}
{"type":"resign","matchId":"…"}
{"type":"getState","matchId":"…"}
{"type":"pong","t":123}
```

### 7.2 Rule vectors (WP1; generated, never hand-edited)
- Path: `spec/rules/vectors/{rulesId}.json`. Files:
  - `n3k3-cap3-v0-w1-none` (default)
  - `n3k3-cap3-v0-w1-pie` (ranked/daily)
  - `n3k3-fifo7-v1-w0-none` (superseded ghost rule; Arena board)
  - `n3k3-fifo7-v1-w0-pie`
  - `n3k3-fifo7-v1-w1-none` (legacy: vanishing piece can win)
  - `n3k3-fifo7-v1-w1-pie`
  - `n3k3-fifo7-v1-w1-ban`
  - `n3k3-fifo7-v0-w1-none`
  - `n3k3-fifo5-v1-w1-none`
  - `n3k3-fifo6-v1-w1-none`
  - `n3k3-fifo9-v0-w1-none` (stuck → Draw)
  - `n3k3-cap3-v1-w1-none`
  - `n4k3-fifo8-v1-w1-none` (rules only, no tablebase block)
- Format:
```jsonc
{ "schema": 1,
  "rules": { /* RuleSetDto JSON */ },
  "cases": [
    { "name": "own-vanishing-piece-does-not-count", "actions": "4,0,8,2,6,3,5,1",
      "expect": { "ply": 8, "placements": 8, "phase": "AwaitingPlacement", "toMove": "X", "outcome": "None", "board": "OOOO.XX.X",
                  "legal": [0,4,7], "nextToVanish": 0, "winLine": null, "swapped": false, "lastVanished": 4 } },
    { "name": "occupied", "actions": "4,4", "error": { "atIndex": 1, "code": "Occupied" } } ],
  "playouts": [ { "seed": "12345", "actions": "…", "finalBoard": "…", "outcome": "XWins" } ],
  "tablebase": { "reachable": 369802, "nonTerminal": 331066,
                 "positions": [ { "actions": "", "value": "Win", "distance": 13 }, { "actions": "1", "value": "Loss", "distance": 12 }, { "actions": "4", "value": "Draw", "distance": 0 } ] } }
```
  The first case was verified by hand against the Python reference; the generated file is still authoritative. `seed` is a decimal string, because ulong does not fit a JSON number safely.
- Case sources:
  - Hand-picked action lists from §0.3 and §4.2 edge cases (window boundary, vacated flag, pie Swap/Keep and seat flip, ban centre, cap removal, K<N win, stuck draw, `WrongActionKind`).
  - 200 seeded random playouts per file.
  - Expectations are computed by the C# engine.
- `spec/rules/summary.json` holds the §0.3 study table. `Engine.Tests` asserts the tablebase reproduces it.
- `spec/clock/schedule.json`: `[{ "timeControl", "gameElapsedMs", "allowanceMs", "suddenDeath", "nextStepAtMs", "nextAllowanceMs" }]`, including every §0.3 clock oracle plus a 0..700 s sweep at 1 s steps for Ranked, CasualX2 and CasualX4.
- `dotnet run --project tools/TicTacToeUltimate.RulesTool -- gen` regenerates everything. `-- gen --check` exits 1 on any difference, and CI runs it. `-- solve <rulesId>` prints stats.

---

## 8. Client

### 8.1 Layering
```
TicTacToeUltimate.Shared (RCL, WP6)     renders state, dispatches intents, owns all CSS/JS/fonts and JS-backed platform defaults
        |
TicTacToeUltimate.Client (net10.0, WP5) stores, RealtimeClient (the only HubConnection), TttApiClient, ClockSync, AdPolicy, Settings
        |   Abstractions/ + State/Contracts/ are frozen (WP0)
Engine + Contracts
Hosts: Web.Client (WASM) and TicTacToeUltimate (MAUI) call AddTttClient + AddTttSharedUi, then register host overrides (WP5).
```

### 8.2 Platform abstractions (verbatim; `TicTacToeUltimate.Client.Abstractions`; WP0)
```csharp
namespace TicTacToeUltimate.Client.Abstractions;

public interface IPlatformInfo { ClientPlatform Platform { get; } bool IsTouch { get; } bool SupportsHaptics { get; } string AppVersion { get; } }
public interface IKeyValueStore            // non-secret prefs; web: localStorage, MAUI: Preferences
{
    ValueTask<string?> GetAsync(string key);
    ValueTask SetAsync(string key, string value);
    ValueTask RemoveAsync(string key);
}
public interface ISecureStore : IKeyValueStore { }   // tokens; web: sessionStorage with localStorage fallback (§8.6), MAUI: SecureStorage
public enum HapticCue { Tap, Place, OpponentPlace, Vanish, Tick, Warning, Win, Loss, Promotion, Error }
public interface IHaptics { bool IsSupported { get; } void Play(HapticCue cue); }
public enum SoundCue { Tap, PlaceX, PlaceO, Vanish, Tick, Warning, SuddenDeath, Win, Loss, Promotion, QuestDone, Emote, Error, MatchFound }
public interface ISoundService
{
    ValueTask InitAsync();
    ValueTask UnlockAsync();                      // call on first user gesture
    void Play(SoundCue cue);
    void SetHeartbeat(int? beatsPerMinute);       // null = off; scheduled inside JS on the audio clock
    void SetMusic(bool on);
    void SetVolumes(double sfx, double music);
}
public sealed record RewardedAdResult(bool Earned, string? Receipt);
public interface IAdProvider
{
    bool IsAvailable { get; }
    Task<bool> ShowInterstitialAsync(CancellationToken ct);
    Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind, CancellationToken ct);
}
public sealed record AdOverlayRequest(string Kind /* "interstitial" | "rewarded" */, RewardKind? Reward, int Seconds);
/// <summary>Implemented by Shared (WP6): renders the stub ad modal; returns true when the countdown completed (false if closed early).</summary>
public interface IAdOverlay { Task<bool> ShowAsync(AdOverlayRequest request, CancellationToken ct); }
public interface IShareService { ValueTask<string> ShareLinkAsync(string title, string text, Uri url); } // "shared" | "copied" | "failed"
public interface IAppLifecycle { bool IsForeground { get; } bool IsOnline { get; } event Action? Resumed; event Action? Suspended; event Action<bool>? NetworkChanged; }
public interface IDeepLinkSource { Uri? InitialLink { get; } event Action<Uri>? LinkOpened; }
public interface IPushService { ValueTask<bool> RequestPermissionAsync(); ValueTask<string?> GetTokenAsync(); }
public sealed record ClientOptions
{
    public required Uri BackendBaseUrl { get; init; }
    public required Uri WebBaseUrl { get; init; }
    public required ClientPlatform Platform { get; init; }
    public string ClientVersion { get; init; } = "1.0";
    public bool IsDevelopment { get; init; }
}
```

### 8.3 Store contracts (verbatim; `TicTacToeUltimate.Client.State`; WP0. WP5 implements and WP6 renders)
```csharp
namespace TicTacToeUltimate.Client.State;

public interface IStore<out TState> where TState : class
{
    TState State { get; }
    event Action? Changed;            // raised after State is replaced; may fire on any thread; UI must InvokeAsync
}

public enum ConnectionPhase { Idle, Connecting, Online, Reconnecting, Offline }
public enum SessionStatus { Initializing, SignedOut, SignedIn }
public sealed record SessionState(SessionStatus Status, MeDto? Me, AppConfigDto? Config, ConnectionPhase Connection,
    int ReconnectAttempt, ImmutableArray<Guid> ActiveMatchIds, int DailyYourTurn, string? Error)
{
    public static SessionState Initial { get; } = new(SessionStatus.Initializing, null, null, ConnectionPhase.Idle, 0, [], 0, null);
    public bool IsGuest => Me?.Kind == AccountKind.Guest;
}
public interface ISessionStore : IStore<SessionState>
{
    Task InitializeAsync();                                   // load config, restore token, connect, Hello; idempotent
    Task<IReadOnlyList<DevUserDto>> GetDevUsersAsync();
    Task SignInDevAsync(Guid userId);
    Task SignInDevNewUserAsync(string username);
    Task SignInGuestAsync();
    Task SignOutAsync();
    Task<CommandAckDto> SetUsernameAsync(string username, bool acceptTos);
    Task<CommandAckDto> UpdateProfileAsync(UpdateProfileDto patch);
}

public enum QueuePhase { Idle, Entering, Searching, Found }
public sealed record QueueState(QueuePhase Phase, TimeControl? TimeControl, QueueStatusDto? Status, MatchFoundDto? Found, string? Error)
{ public static QueueState Idle { get; } = new(QueuePhase.Idle, null, null, null, null); }
public interface IQueueStore : IStore<QueueState>
{
    Task EnterAsync(TimeControl timeControl);
    Task LeaveAsync();
    void Acknowledge();                                       // Found -> Idle after navigation
}

public enum ClockUrgency { Idle, Normal, Warning /* <= 5 s */, Critical /* <= 3 s */ }
public sealed record ClockView(int? RemainingMs, int? AllowanceMs, int GameElapsedMs, bool SuddenDeath,
    int? NextAllowanceMs, int? NextStepInMs, ClockUrgency Urgency, int ToMoveSeat);
public sealed record EmoteView(int Seat, string EmoteId, string Text, long ShownAtMs);
public sealed record MatchView(
    Guid MatchId,
    MatchSnapshotDto Snapshot,                // last authoritative snapshot with events folded in
    GameState Engine,                         // Rules.Replay of Snapshot.Actions (authoritative)
    int? PendingCell,                         // optimistic placement awaiting ack
    int? MySeat, bool IsSpectator, bool IsMyTurn,
    ImmutableArray<int> ThreatCells,          // opponent ImmediateWins on my turn; empty if hints off or Ranked/Daily
    ImmutableArray<EmoteView> RecentEmotes,
    MatchResultDto? Result, PostGameSummaryDto? Summary,
    bool RematchOfferedByMe, bool RematchOfferedByOpponent, Guid? RematchMatchId,
    bool Resyncing, string? LastError);
public sealed record MatchesState(ImmutableDictionary<Guid, MatchView> Matches)
{ public static MatchesState Empty { get; } = new(ImmutableDictionary<Guid, MatchView>.Empty); }
public interface IMatchStore : IStore<MatchesState>
{
    Task<MatchView?> OpenAsync(Guid matchId);                 // JoinMatch; null + LastError on failure
    void Close(Guid matchId);                                 // LeaveMatch, drop from state
    Task PlaceAsync(Guid matchId, int cell);
    Task DecideSwapAsync(Guid matchId, bool swap);
    Task ResignAsync(Guid matchId);
    Task SendEmoteAsync(Guid matchId, string emoteId);
    Task OfferRematchAsync(Guid matchId);
    ClockView? GetClock(Guid matchId);                        // live value computed from server time
    event Action? ClockTick;                                  // 10 Hz, only while any opened match has a running clock
}

public sealed record SocialState(ImmutableArray<FriendDto> Friends, ImmutableArray<FriendRequestDto> Requests,
    ImmutableArray<PlayerCardDto> Blocked, ImmutableArray<ChallengeDto> IncomingChallenges,
    ImmutableDictionary<Guid, ImmutableArray<DirectMessageDto>> Threads, bool Loaded, string? Error)
{ public static SocialState Empty { get; } = new([], [], [], [], ImmutableDictionary<Guid, ImmutableArray<DirectMessageDto>>.Empty, false, null); }
public interface ISocialStore : IStore<SocialState>
{
    Task LoadAsync();
    Task<IReadOnlyList<PlayerCardDto>> SearchAsync(string prefix);
    Task<CommandAckDto> SendRequestAsync(string username);
    Task<CommandAckDto> RespondAsync(Guid requestId, bool accept);
    Task RemoveFriendAsync(Guid userId);
    Task BlockAsync(Guid userId);
    Task UnblockAsync(Guid userId);
    Task LoadThreadAsync(Guid friendId, long? beforeId = null);
    Task<CommandAckDto> SendMessageAsync(Guid friendId, string body);
    Task MarkReadAsync(Guid friendId);
    Task<ChallengeDto> CreateChallengeAsync(CreateChallengeDto request);
    Task<ChallengeDto?> GetChallengeAsync(string code);
    Task<AcceptChallengeResultDto> AcceptChallengeAsync(string code);
    Task DeclineChallengeAsync(string code);
}

public sealed record ProgressState(ProgressDto? Progress, bool Loaded)
{ public static ProgressState Empty { get; } = new(null, false); }
public interface IProgressStore : IStore<ProgressState>
{
    Task LoadAsync();
    /// <summary>Shows a rewarded ad via IAdCoordinator, then POST /api/rewards/claim. Returns the claim result.</summary>
    Task<RewardClaimResultDto> ClaimRewardAsync(RewardKind kind, Guid? gameId = null);
}

/// <summary>Stateless queries (no caching contract).</summary>
public interface IGameQueries
{
    Task<ProfileDto?> GetProfileAsync(string username);
    Task<IReadOnlyList<GameSummaryDto>> GetHistoryAsync(Guid userId, DateTimeOffset? before = null, int take = 20);
    Task<GameRecordDto?> GetGameAsync(Guid gameId);
    Task<(GameAnalysisDto? Analysis, string? Error)> GetAnalysisAsync(Guid gameId);
    Task<LeaderboardPageDto> GetLeaderboardAsync(RatingPool pool, LeaderboardScope scope, string? country, int page);
    Task<IReadOnlyList<DailyGameDto>> GetDailyGamesAsync();
    Task<IReadOnlyList<LiveMatchDto>> GetLiveFriendMatchesAsync();
}

public enum ThemePreference { System, Dark, Light }
public enum MotionPreference { System, Reduced, Full }
public sealed record SettingsState(ThemePreference Theme, bool Sfx, bool Music, bool Haptics, bool ImpactFrames,
    MotionPreference Motion, bool ThreatHints, bool MuteOpponentEmotes, double SfxVolume, double MusicVolume)
{ public static SettingsState Default { get; } = new(ThemePreference.Dark, true, false, true, true, MotionPreference.System, true, false, 0.8, 0.5); }
public interface ISettingsStore : IStore<SettingsState>
{
    Task LoadAsync();
    Task UpdateAsync(Func<SettingsState, SettingsState> change);   // persists via IKeyValueStore, applies to ISoundService
}

public interface IAdCoordinator
{
    bool BannersAllowed { get; }                     // false while a match route is shown or queueing
    void SetMatchRouteActive(bool active);           // called by MatchLayout (WP6)
    Task OnGameCompletedAsync(GameResultForUser result);   // AdPolicy decides; never shows during a match route
    Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind);
}
```
Implementation names (WP5, `TicTacToeUltimate.Client`):
- `Services/`: `TttApiClient`, `RealtimeClient`, `TokenStore`, `ClockSync`, `RetryForeverPolicy`, `StubAdProvider : IAdProvider` (uses `IAdOverlay`; receipt `stub:{kind}:{guid}`), `AdPolicy` (pure), `AdCoordinator`.
- `Stores/`: `SessionStore`, `QueueStore`, `MatchStore`, `SocialStore`, `ProgressStore`, `GameQueries`, `SettingsStore`.
- `ClientServiceCollectionExtensions.AddTttClient(this IServiceCollection, ClientOptions)`: registers all of the above as singletons.

Behaviour WP5 must implement:
- `RealtimeClient`:
  - URL `BackendBaseUrl + HubRoutes.Game`. `AccessTokenProvider` comes from `TokenStore`. Default transports (WebSockets first).
  - `WithAutomaticReconnect(RetryForeverPolicy: 0, 1, 2, 5, 10 s, then 10 s ± 20% forever)`.
  - JSON uses `ContractsJsonContext`. It answers `Ping` with `Pong` immediately.
  - `Reconnected` → `Hello` → `OpenAsync` again for every open match. `Closed` → restart the loop while signed in.
  - `IAppLifecycle.Resumed`/`NetworkChanged(true)` → immediate reconnect attempt.
- `ClockSync`: 5 `ServerTime()` samples at connect and 1 every 60 s. `offset = serverMs − (t0 + t1)/2` from the sample with the smallest RTT among the last 8.
- `MatchStore`:
  - Applies envelopes in `Seq` order and drops `Seq <= last`. On a gap it buffers up to 2 s, then calls `OpenAsync` again (resync, `Resyncing = true`).
  - Optimistic placement: validate with `Rules.Apply` locally, set `PendingCell`, call `SubmitAction(ExpectedPly = Snapshot.Ply, ActionId = Guid.NewGuid())`. Clear on the ack or the `ActionApplied` event. On rejection, roll back, set `LastError`, play `HapticCue.Error`.
  - `GetClock`: `RemainingMs = DeadlineAtMs − ClockSync.ServerNowMs()`, clamped at 0. `Urgency`: Idle when it is not that seat's turn; Warning at ≤ 5 s; Critical at ≤ 3 s. `NextStepInMs = NextStepAtGameElapsedMs − (GameElapsedMsAtTurnStart + (now − TurnStartedAtMs))`.
  - Threat hints are on only if `Settings.ThreatHints` and the mode is not Ranked or Daily.
- Sounds and haptics are triggered by `MatchStore` on events: place, vanish, opponent place, warning tick each second at ≤ 5 s, sudden-death start, win, loss. Heartbeat bpm = `70 + 10·(10 − allowanceSeconds)`, clamped to [70, 150], only in sudden death on your turn; off otherwise.
- `SessionStore`: the token is persisted in `ISecureStore` under key `ttt.token`. The device id for guests is in `IKeyValueStore` under `ttt.deviceId`. On a 401 from REST or negotiate: Dev → sign out to `/signin`; Guest → re-mint by device id.

### 8.4 Shared UI (`/TicTacToeUltimate.Shared`, WP6)
```
Shared/
  _Imports.razor, Routes.razor (root component for both hosts; Router + AuthGate + NotFound)
  SharedUiServiceCollectionExtensions.cs   AddTttSharedUi(): JsKeyValueStore, JsSecureStore, JsHaptics, JsSoundService,
                                           JsShareService, JsLifecycle, NoopDeepLinkSource, NoopPushService, AdOverlayService : IAdOverlay
                                           (all TryAdd*; hosts register their overrides afterwards with Replace or AddSingleton, and the last registration wins; see §8.6)
  Platform/  Js*.cs wrappers over wwwroot/js/ttt.js (ES module, loaded via IJSRuntime import)
  Components/StoreComponent.cs             base: subscribes to IStore.Changed -> InvokeAsync(StateHasChanged); disposes
  Components/Design/   Button, IconButton, Card, Chip, ProgressBar, Avatar, PresenceDot, TierBadge, StreakPill, PieceGlyph,
                       Sheet, Toast, Tabs, TextField, CountryFlag, BotBadge
  Components/Game/     Board, BoardCell, WinLine, PlayerCard, PlayerClock, MatchHeader, SuddenDeathBar, MatchActions,
                       EmotePicker, EmoteBubble, ResignSheet, MatchMenuSheet, SwapDecisionSheet, ImpactFrame,
                       ConnectionBanner, MiniBoard, ReplayScrubber, MoveList, AnalysisBadge, ThreatCallout
  Components/Home/     PlayHero, ModeGrid, QuestCard, FriendsOnlineRow, AdSlot
  Components/Social/   PlayerSearch, ChallengeCard, FriendRow, FriendRequestRow, InviteLinkButton, MessageList, Composer
  Components/Profile/  ProfileHeader, StatGrid, TierLadder, RecentGames, GameRow, LeaderboardRow
  Layout/              AppShell (BottomNav < 900px, Sidebar >= 900px; both rendered, CSS shows one), MatchLayout (no nav,
                       cascades AdsAllowed=false, calls IAdCoordinator.SetMatchRouteActive), AdOverlayHost, ToastHost
  Pages/               see the route table
  Fakes/               fake stores for bUnit tests
  wwwroot/css/tokens.css      port of /design/system/tokens.json: :root = dark, [data-theme=light], prefers-color-scheme for System
  wwwroot/css/components.css  port of /design/system/components/bundle.css
  wwwroot/css/app.css, motion.css (place 180ms, vanish 220ms, win line, clock pulse, impact frame; all off under reduced motion)
  wwwroot/fonts/*.woff2       self-hosted OFL: Lilita One, Nunito 600/700/800, Chakra Petch 600/700 (fallback stacks per tokens)
  wwwroot/js/ttt.js           storage, vibrate, WebAudio-synthesized SFX + heartbeat scheduler, share, lifecycle, theme, focus
```

| Route | Page | Mockup | Notes |
|---|---|---|---|
| `/` | HomePage | Main | StreakPill, PlayHero (Ranked tier + rating), ModeGrid (Casual ×2·×4, Daily "N your move", Unlimited), QuestCard, FriendsOnlineRow, AdSlot |
| `/play` | PlayPage | — | mode picker → queue sheet (elapsed, ±window, cancel; "Guest pool" for guests); "Play a friend" (create link) |
| `/match/{id:guid}` | MatchPage (MatchLayout) | Match, SuddenDeath | opponent card top, board, own card bottom, emote/resign/menu; spectator mode when `IsSpectator`; on end shows the result overlay, then navigates to `/result/{id}` |
| `/result/{id:guid}` | ResultPage | Result | VICTORY/DEFEAT/DRAW/ABORTED, rating delta count-up, tier progress, streak/quest, analysis teaser (rewarded), New match / Rematch (if allowed) / Home; leaving runs `IAdCoordinator.OnGameCompletedAsync` |
| `/replay/{id:guid}` | ReplayPage | — | read-only board, scrubber, move list, analysis badges (reward-gated) |
| `/daily` | DailyPage | — | your-move-first list |
| `/social` | SocialPage | Social | search, incoming challenge, friends (Challenge / Watch / Daily), invite by link |
| `/social/chat/{userId:guid}` | ChatPage | — | mutual friends only |
| `/leaderboards` | LeaderboardsPage | — | Global / Friends / Country / Master; pool Ranked / Daily |
| `/profile`, `/u/{username}` | ProfilePage | Profile | header, stat grid, tier ladder, recent games; others: add friend / challenge / block |
| `/history` | HistoryPage | — | |
| `/c/{code}` | ChallengePage | — | preview → accept (sign in or guest first) |
| `/signin` | SignInPage | — | Dev: user picker, create user, "Play as guest". Supabase buttons only when `Config.SupabaseAuth` (stubs) |
| `/onboarding` | OnboardingPage | — | username (live validation), country, avatar preset, 13+ ToS checkbox |
| `/settings` | SettingsPage | — | theme, SFX, music, haptics, impact frames, motion, threat hints, mute emotes, account, sign out |

Mobile frames are 390×844. Board size is `min(92vw, 60vh, 520px)`, with cells at least 44 px. Pieces are always glyphs and never told apart by colour alone. Cells have an aria-label like "Row 1, column 3, O, vanishes next". There is an `aria-live` move announcer.

### 8.5 JS module contract (`_content/TicTacToeUltimate.Shared/js/ttt.js`, WP6)
```js
export function storageGet(area /* 'local'|'session' */, key) {}   export function storageSet(area, key, value) {}
export function storageRemove(area, key) {}
export function vibrate(pattern) {}                                 // returns bool
export function audioInit() {}  export function audioUnlock() {}    export function audioPlay(cue) {}
export function audioHeartbeat(bpm /* or null */) {}               export function audioMusic(on) {}  export function audioVolumes(sfx, music) {}
export function share(title, text, url) {}                          // 'shared'|'copied'|'failed'
export function lifecycleSubscribe(dotnetRef) {}                    // calls OnVisibility(bool), OnOnline(bool)
export function setTheme(theme) {}  export function setMotion(mode) {}
export function focusCell(boardEl, index) {}
```

### 8.6 Hosts (WP5)
- **Web** (`TicTacToeUltimate.Web/Program.cs`):
  - `AddRazorComponents().AddInteractiveWebAssemblyComponents()` → `MapStaticAssets()` → `MapRazorComponents<App>().AddInteractiveWebAssemblyRenderMode().AddAdditionalAssemblies(typeof(TicTacToeUltimate.Shared.Routes).Assembly)`.
  - `Components/App.razor`: `<Routes @rendermode="new InteractiveWebAssemblyRenderMode(prerender: false)" />`, font preloads, CSS links `_content/TicTacToeUltimate.Shared/css/{tokens,components,app,motion}.css`, an inline theme bootstrap script (`data-theme` from localStorage before first paint), and a purple splash.
  - It serves `/c/{code}` like any route.
- **Web.Client** (`Program.cs`):
  - `ClientOptions` comes from `wwwroot/appsettings.json`: `{ "Backend": { "BaseUrl": "http://localhost:5091" }, "Web": { "BaseUrl": "http://localhost:5028" } }`.
  - Calls `AddTttClient(opts)`, then `AddTttSharedUi()`, then `IPlatformInfo` (WebPlatformInfo), `IAdProvider` (StubAdProvider), `IAppLifecycle` (JsLifecycle).
  - Web `ISecureStore` = `JsSecureStore`. It reads sessionStorage first (per tab), then localStorage; sign-in writes both. The Dev-only `?as={username}` query signs that tab in as the fixture user and writes sessionStorage only, so two tabs can play each other.
- **MAUI** (`TicTacToeUltimate/`):
  - TFMs unchanged; the `Plugin.AdMob` reference is gone (WP0). `ApplicationTitle` = "TTT Ultimate".
  - `MauiIcon` and `MauiSplashScreen` colour `#7442ef`, page background `#120a22`.
  - `MainPage.xaml`: a full-page `BlazorWebView` with `RootComponent` `shared:Routes`.
  - `MauiProgram`: `AddMauiBlazorWebView()`. `ClientOptions` comes from embedded `Resources/Raw/appsettings.json` (`Backend:BaseUrl`). In Debug, the default is the platform-specific dev URL: Android emulator `http://10.0.2.2:5091`; iOS simulator and MacCatalyst `http://localhost:5091`. Then `AddTttClient`, `AddTttSharedUi`, then **override** with `services.Replace(...)`: `MauiSecureStore` (SecureStorage), `MauiKeyValueStore` (Preferences), `MauiHaptics` (HapticFeedback/Vibration; Catalyst no-op), `MauiPlatformInfo`, `MauiLifecycle` (Window events + Connectivity), `MauiDeepLinkSource` (scheme `tttu`), `MauiShareService`, `StubPushService`, `StubAdProvider`.
  - No startup probe, no `.Result`, no forced LongPolling. Debug-only `BlazorWebViewDeveloperTools`.
  - Android: Debug `network_security_config.xml` allows cleartext to `10.0.2.2` and `localhost`; the manifest adds INTERNET, ACCESS_NETWORK_STATE, VIBRATE and an intent filter for `tttu://c/*`. iOS/MacCatalyst: `NSAppTransportSecurity/NSAllowsLocalNetworking = true`, `CFBundleURLTypes` for `tttu`. Catalyst keeps `com.apple.security.network.client`.
  - `wwwroot/index.html`: `viewport-fit=cover`, Shared CSS links, `env(safe-area-inset-*)` padding, background `#120a22`.
  - Portrait lock on phones (iOS Info.plist orientations, Android `ScreenOrientation.Portrait` in MainActivity).
- `scripts/dev.sh` (WP0): starts Server (`--urls http://localhost:5091`) and Web (`--urls http://localhost:5028`) in parallel and prints both URLs.

---

## 9. Postgres schema (WP3; `supabase/migrations/20261015000001_init.sql` … `…05_rls.sql`)
```sql
create extension if not exists citext;

create table players (
  id uuid primary key,                                   -- = JWT sub (Supabase auth uid, dev id, or bot id); NO fk to auth.users
  kind text not null check (kind in ('guest','account','bot')),
  username citext unique check (username is null or (username::text ~ '^[a-z0-9_]{3,16}$' and username::text = lower(username::text))),
  display_name text not null,
  avatar_id text not null default 'a1',
  country char(2),
  time_zone text not null default 'UTC',
  renames_left smallint not null default 1,
  tos_accepted_at timestamptz,
  device_id text unique,
  created_at timestamptz not null default now(),
  last_seen_at timestamptz not null default now());

create table ratings (
  user_id uuid not null references players(id) on delete cascade,
  pool text not null check (pool in ('Ranked','CasualX2','CasualX4','Unlimited','Daily')),
  rating double precision not null default 1200, rd double precision not null default 350,
  volatility double precision not null default 0.06, games int not null default 0, wins int not null default 0,
  losses int not null default 0, draws int not null default 0, peak double precision not null default 1200,
  last_game_at timestamptz, primary key (user_id, pool));
create index ratings_board on ratings (pool, rating desc);

create table games (
  id uuid primary key, mode text not null, time_control text not null, rated boolean not null,
  rules_id text not null, rules jsonb not null,
  seed bigint not null,                                  -- ulong stored as unchecked((long)seed)
  seat_a uuid not null references players(id), seat_b uuid not null references players(id),
  swapped boolean not null default false, origin text not null,
  outcome text not null, end_reason text not null, winner_seat smallint, win_line smallint[] not null default '{}',
  detail text, started_at timestamptz not null, ended_at timestamptz not null,
  rating_snapshots jsonb not null default '[]');
create index games_seat_a on games (seat_a, ended_at desc);
create index games_seat_b on games (seat_b, ended_at desc);

create table game_actions (                               -- append-only; shared by finished games and live daily matches
  game_id uuid not null, ply int not null, seat smallint not null, kind text not null, cell smallint,
  think_ms int not null, auto boolean not null default false, vanished_cell smallint, at timestamptz not null,
  primary key (game_id, ply));

create table daily_matches (                              -- live correspondence state
  id uuid primary key, setup jsonb not null, version bigint not null default 0,
  turn_deadline timestamptz, ended boolean not null default false, started_at timestamptz not null,
  seat_a uuid not null, seat_b uuid not null);
create index daily_due on daily_matches (turn_deadline) where not ended;
create index daily_a on daily_matches (seat_a) where not ended;
create index daily_b on daily_matches (seat_b) where not ended;

create table friend_requests (id uuid primary key, from_id uuid not null, to_id uuid not null,
  created_at timestamptz not null default now(), unique (from_id, to_id));
create table friendships (user_lo uuid not null, user_hi uuid not null, created_at timestamptz not null default now(),
  primary key (user_lo, user_hi), check (user_lo < user_hi));
create table blocks (blocker uuid not null, blocked uuid not null, created_at timestamptz not null default now(),
  primary key (blocker, blocked));
create table direct_messages (id bigserial primary key, sender uuid not null, recipient uuid not null,
  body text not null check (char_length(body) between 1 and 500), sent_at timestamptz not null default now(), read_at timestamptz);
create index dm_thread on direct_messages (least(sender, recipient), greatest(sender, recipient), id desc);

create table streaks (user_id uuid primary key, current int not null default 0, best int not null default 0,
  last_played_day date, freezes smallint not null default 0 check (freezes between 0 and 2));
create table quest_days (user_id uuid not null, day date not null, wins int not null default 0,
  freeze_awarded boolean not null default false, bonus_target int, bonus_claimed boolean not null default false,
  primary key (user_id, day));

create table challenges (code text primary key, creator_id uuid not null, target_user_id uuid, time_control text not null,
  rules_id text not null, status text not null, match_id uuid, accepted_by uuid,
  created_at timestamptz not null default now(), expires_at timestamptz not null);
create table bot_keys (key_id text primary key, bot_user_id uuid not null references players(id), owner_user_id uuid not null,
  bot_name text not null, secret_hash bytea not null, created_at timestamptz not null default now(), revoked_at timestamptz);
create table reward_grants (nonce text primary key, user_id uuid not null, kind text not null, game_id uuid,
  granted_at timestamptz not null default now());
create table seasons (id serial primary key, starts_at timestamptz not null, ends_at timestamptz not null);
create table master_season_results (season_id int not null references seasons(id), user_id uuid not null,
  final_rating double precision not null, rank int not null, primary key (season_id, user_id));

-- 0005_rls.sql: alter table <each> enable row level security;  -- no anon/authenticated policies. Server uses the service role.
```
Postgres repositories (`Persistence/Postgres/*`) use Npgsql and Dapper. They are registered only when `Provider=Postgres` and pass the same contract test suites as InMemory (§10).

---

## 10. Testing strategy

All test projects use xunit.v3 with `dotnet test` and need no network or external services. Time-dependent code takes `TimeProvider`; tests use `FakeTimeProvider`. Every repository interface has an **abstract contract test class** (`tests/TicTacToeUltimate.Server.Tests/Persistence/Contracts/*ContractTests.cs`, WP3). It runs against InMemory always, and against Postgres only when env `TTT_TEST_PG` is set.

| Project | Owner(s) | Must include |
|---|---|---|
| Engine.Tests | WP1 | vector conformance (every `spec/rules/vectors/*.json`); §0.3 rule cases; Replay == step-wise Apply; illegal actions do not mutate; `Pieces.Length <= capacity`; SplitMix64 determinism; **study table** (§0.3) reproduced by `Tablebase` for every row whose ruleset is expressible; Default builds < 3 s; MoveClock oracles + `schedule.json`; `RuleSet.Id` round-trip |
| Server.Tests/Analysis | WP1 | cache warming states; pie normalization; analyzer blunder marking on a known game |
| Server.Tests/{Matches,Clocks,Matchmaking,Challenges,Presence} | WP2 | allowance fixed at turn start; timeout exactly at `allowance+credit` vs 1 tick before; **a move stamped before the deadline is accepted even if its Tick was posted 1 tick later** (ingress lock); credit drains the budget per move; casual auto-move determinism; ply-0 abort (timeout, resign); pie swap remaps seats; `stale_ply`; ActionId dedupe; **storm: 1,000 parallel submits from both seats over 3 attachments → exactly one accepted per ply, strictly increasing Seq, no exceptions**; snapshot + later events == actor state; Daily rehydration single-flight (two concurrent activations → one actor); sweeper timeout; Unlimited disconnect forfeit; matchmaking window widening at 3/6/9 s, no double pairing, guest fallback at 10 s, bot fallback; challenge CAS (two acceptors → one wins), expiry |
| Server.Tests/{Auth,Persistence,Ratings,Progression,Social,Rewards,Api} | WP3 | Glicko paper vector; provisional; tier bands (1552 → Gold II, 48 to next); season reset only Master; streak across time zones (UTC−10, UTC+14), freeze consumption, cap 2, quest freeze award; PostGameProcessor idempotent (same match twice → one rating change); rating serialization across concurrent completions; username rules + one rename; DM only between mutual friends and not blocked; dev token / guest token / upgrade keeps id; Production + Dev enabled → startup throws; repository contract suites |
| Server.IntegrationTests | WP4 | `WebApplicationFactory<Program>` (InMemory, Dev auth, `FakeTimeProvider` replaced in DI) with real `HubConnection`s over the TestServer handler: two accounts queue Ranked → MatchFound → pie decision → forced line to win → both get identical Seq streams → PostGameSummary with rating change; reconnect mid-game → JoinMatch snapshot, clock kept running; spectator (friend) gets events, non-friend refused; challenge by code; ranked rematch refused, casual rematch works; guest refused Ranked; DM flow; bot WS (`TestServer.CreateWebSocketClient`): bad key 4401, seek → bot fallback → full casual game, concurrent sends serialized, stale ply error |
| Client.Tests | WP5 | MatchStore ordering/gap resync/optimistic rollback; ClockSync offset with jittered samples; ClockView urgency thresholds and NextStepInMs ("8s → 7s in 5s" at game 1:31); AdPolicy (every 3, skip after 2 losses, never on match route); RetryForeverPolicy schedule; Settings persistence; heartbeat bpm mapping |
| Shared.Tests | WP6 | bUnit: Board renders N×N, faded next-to-vanish, win line, aria labels; PlayerClock classes idle/normal/warn/sd; AdSlot renders nothing under MatchLayout; ResultPage hides Rematch when Rated; MatchPage disables input when not your turn; **tokens.css contains every token of `/design/system/tokens.json` in both themes** |

### 10.1 Build and test commands (the full matrix every merge must pass)
```bash
dotnet build TicTacToeUltimate.Web.slnf
dotnet test  TicTacToeUltimate.Web.slnf
dotnet run --project tools/TicTacToeUltimate.RulesTool -- gen --check
dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-android
dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-maccatalyst
# or all of the above:
scripts/build-all.sh          # exits non-zero on the first failure; no skips
```
Local smoke test: `scripts/dev.sh`, then open http://localhost:5028 in two tabs (`?as=ali_xo` and `?as=kestrel_99`) and play Ranked into sudden death. Run the MAUI app on the iOS simulator, the Android emulator and MacCatalyst against the same server, playing against a web tab.

---

## 11. Non-goals for this rebuild
Cosmetics, IAP, real AdMob, real push delivery, Supabase OAuth UI flows beyond stub buttons, the Go bot itself, a cheat-detection pipeline, repeat-pair cooldowns, featured/public spectating, spectator delay, move caps, NoMoves policies other than Draw, a component gallery page, sample bots.

---

## 12. Open questions (defaults in effect)
1. 4x4 variants as an additional mode (study ongoing). 3x3 original rule is final for launch. Under `w0` (AGENTS.md, `tools/study/results-4x4.txt`) the only non-trivial candidate is 4-in-a-row OR 2x2 square, GlobalFifo W=9, vacated playable: X wins in 29, pie is a forced win for the chooser. W=7 is 100% draws under `w0`. Adopting it needs a square win shape in `RuleSet`/`LineTable` (contract request) and a new ruleset id; the `n4k3-fifo8-v1-w1-none` vector in §7.2 is a rules-only engine test, not a product candidate.
2. Tier bands (D12).
3. Whether an O timeout on the first action should abort (D9).
4. Threat hints in Ranked (D15) and a ranked spectator delay (D16).
5. Chat retention (§6.7).
6. Whether bots may be rated (`Bots:Ranked`, default false).
7. Whether guests may accept challenge links (D13, default yes).

---

## 13. Work packages

### 13.1 Rules of engagement
- **Ownership is by path.** A WP writes only inside its owned paths. Everything else is read-only to it. Owned path lists are disjoint.
- **Frozen files** are owned by WP0: Contracts project, `Server/Abstractions/**`, `Server/Program.cs`, `Server/Hosting/**`, `Server/Realtime/GameHub.cs`, Server csproj and appsettings, `Client/Abstractions/**`, `Client/State/Contracts/**` (the §8.3 types live in `TicTacToeUltimate.Client/State/Contracts/*.cs`), `Directory.*`, `global.json`, `.sln`, `.slnf`, `Server.Tests` csproj, `scripts/**`. They change only through §13.4.
- **Stub hand-off.** WP0 creates compiling stub files inside other WPs' folders (extension methods, `GameHub.<Area>.cs` with `throw new HubException("not_implemented")` bodies, `Engine` signatures). From the moment WP0 merges, the folder owner owns those files.
- **Packages.** A project's csproj belongs to its owner. `PackageReference` items may be added only for packages already versioned in `Directory.Packages.props`.
- **Notes, not AGENTS.md.** Each WP records decisions and deviations in `docs/notes/WPn.md` (owned). The lead folds them into AGENTS.md at integration.
- **Fakes over waiting.** Tests use in-file fakes of other WPs' interfaces. Never block on another WP's code; code against the frozen interfaces.
- **Definition of done (every WP):**
  - §10.1 is fully green on the integration branch after your merge, including all three MAUI TFMs.
  - New public APIs have XML docs.
  - No `Console.WriteLine` (use `ILogger`).
  - No `DateTime.Now`/`UtcNow` in server or engine code (use `TimeProvider`).
  - No `.Result`/`.Wait()`.
  - No new external service dependency.

### 13.2 Branches and merge order
- WP0 commits to `rebuild/pvp-foundation`. Each WPn branches from that commit as `rebuild/wpN-<slug>`.
- Merge order into `rebuild/pvp-foundation`: **WP1 → WP3 → WP2 → WP4 → WP5 → WP6**. Run the full §10.1 matrix after each merge.
- Ownership is disjoint, so merges should not conflict. If one does, the later merger resolves it without editing the other WP's files.

### 13.3 Packages

#### WP0 Foundation and contracts (lead; sequential; must finish before WP1–WP6 start)
- **Owns:**
  - `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `TicTacToeUltimate.sln`, `TicTacToeUltimate.Web.slnf`, `.gitignore`, `README.md` (initial), `scripts/**`, `design/**`, `tools/study/**`, `AGENTS.md`, `docs/ARCHITECTURE.md`
  - `TicTacToeUltimate.Contracts/**`
  - `TicTacToeUltimate.Server/{TicTacToeUltimate.Server.csproj, Program.cs, appsettings*.json, Properties/**, Hosting/**, Abstractions/**, Realtime/GameHub.cs}`
  - `TicTacToeUltimate.Client/{Abstractions/**, State/Contracts/**}`
  - `tests/TicTacToeUltimate.Server.Tests/TicTacToeUltimate.Server.Tests.csproj`
  - Initial creation of every other project, csproj and stub file (handed off per §13.1)
  - The cleanup in §3.4
- **Deliverables:**
  1. Cleanup (§3.4) and the toolchain files (§3.1–3.3).
  2. Projects and references per §2. The Engine API (§4) compiles: enums and records are real; logic bodies throw `NotImplementedException`; `RuleSet.Default`/`RankedDefault` are real.
  3. Contracts verbatim (§5) with the full `ContractsJsonContext`.
  4. Server: `Program.cs` (§6.2), `Hosting/**` (`TttOptions`, `AddTttCore`/`UseTttCore`/`MapTttCore`, `UserNotifier`, `SubUserIdProvider`, null fallbacks, `LoggingPushSender`, `DefaultPlayerCardService`), Abstractions verbatim (§6.3), GameHub root (§6.4) plus stub partials, stub `AddTttX`/`MapTttX` in each owner folder, appsettings (§6.10).
  5. Client: §8.2 and §8.3 verbatim, plus a stub `AddTttClient`.
  6. Shared: placeholder `Routes.razor` (a "Rebuilding…" page), stub `AddTttSharedUi`.
  7. Web, Web.Client and MAUI rewired to compile against the stubs.
  8. Five test projects, each with one passing smoke test.
  9. RulesTool console stub.
  10. `scripts/dev.sh` and `scripts/build-all.sh`.
  11. Append the §1 decisions to AGENTS.md (Decisions, 2026-10-15, "Rebuild spec").
- **Depends on:** nothing.
- **Acceptance:**
  - Every command in §10.1 succeeds except `gen --check` (absent until WP1).
  - `dotnet run --project TicTacToeUltimate.Server` serves `GET /health` = 200.
  - `git ls-files | grep -E 'BackendWebAPI|Plugin.AdMob|FormFactor|bootstrap'` is empty.
- **Test commands:** `scripts/build-all.sh`; `dotnet test TicTacToeUltimate.Web.slnf`.

#### WP1 Engine, tablebase, analysis, rule and clock vectors
- **Owns:** `TicTacToeUltimate.Engine/**` (incl. csproj), `tools/TicTacToeUltimate.RulesTool/**`, `spec/rules/**`, `spec/clock/**`, `docs/rules.md`, `TicTacToeUltimate.Server/Analysis/**`, `tests/TicTacToeUltimate.Engine.Tests/**`, `tests/TicTacToeUltimate.Server.Tests/Analysis/**`, `docs/notes/WP1.md`.
- **Implements:** §4 (all), `ITablebaseCache`, `IAnalysisService`, `AddTttAnalysis`, RulesTool `gen`/`gen --check`/`solve`.
- **Depends on:** WP0 only.
- **Acceptance:**
  - Every §0.3 rule case passes.
  - Every row of the §0.3 study table is reproduced: reachable, non-terminal, start value and distance; for Default, also per-opening values and the pie verdict.
  - Default builds in under 3 s.
  - The clock oracles pass.
  - `gen --check` is clean.
  - Tablebase cache warm-up at server start does not delay `/health`.
  - `docs/rules.md` restates §4.2 normatively.
- **Test commands:** `dotnet test tests/TicTacToeUltimate.Engine.Tests`; `dotnet test tests/TicTacToeUltimate.Server.Tests --filter "FullyQualifiedName~Analysis"`; `dotnet run --project tools/TicTacToeUltimate.RulesTool -- gen --check`.

#### WP2 Realtime match core (actors, clocks, matchmaking, challenges, presence, hub match surface)
- **Owns:** `TicTacToeUltimate.Server/{Matches,Matchmaking,Challenges,Presence}/**`, `TicTacToeUltimate.Server/Realtime/GameHub.{Session,Queue,Match,Challenge}.cs`, `tests/TicTacToeUltimate.Server.Tests/{Matches,Clocks,Matchmaking,Challenges,Presence}/**`, `docs/notes/WP2.md`.
- **Implements:** §6.5, §6.6, `IMatchGateway`, `IChallengeService`, `IPresenceTracker`, `AddTttMatches`/`MapTttMatches`, RttPinger, DailyDeadlineSweeper, hub methods (Session/Queue/Match/Challenge), REST `/api/challenges/{code}`, `/api/daily`, `/api/live`.
- **Consumes (frozen interfaces):** Engine, `IDailyMatchRepository`, `IChallengeRepository`, `IUserRepository`, `IRatingRepository`, `IFriendRepository` (spectate, targeted challenges), `IPlayerCardService`, `IMatchResultSink`, `IPresenceSink`, `IBotPool`, `IUserNotifier`, `IPushSender`.
- **Depends on:** WP0. Tests use fakes until WP1 and WP3 merge.
- **Acceptance:** every WP2 row in §10 passes on `FakeTimeProvider`, including the ingress-race test and the 1,000-submit storm. No `lock` around match state other than the ingress lock.
- **Test commands:** `dotnet test tests/TicTacToeUltimate.Server.Tests --filter "FullyQualifiedName~Matches|FullyQualifiedName~Clocks|FullyQualifiedName~Matchmaking|FullyQualifiedName~Challenges|FullyQualifiedName~Presence"`.

#### WP3 Platform services (auth, persistence, ratings, progression, social, rewards, REST, Supabase)
- **Owns:** `TicTacToeUltimate.Server/{Auth,Persistence,Ratings,Progression,Social,Rewards,Api}/**`, `TicTacToeUltimate.Server/Realtime/GameHub.Social.cs`, `supabase/**`, `tests/TicTacToeUltimate.Server.Tests/{Auth,Persistence,Ratings,Progression,Social,Rewards,Api}/**`, `docs/notes/WP3.md`.
- **Implements:** §6.7, §9, every repository in §6.3 (InMemory and Postgres), `IMatchResultSink` (PostGameProcessor), `IPresenceSink`, `IPlayerCardService`, `AddTttPlatform`/`MapTttPlatform`, policies, DevFixtures, `seed.sql`.
- **Consumes:** Engine (fixture playouts), `ITablebaseCache`/`IAnalysisService`, `IPresenceTracker`, `IUserNotifier`.
- **Depends on:** WP0.
- **Acceptance:**
  - Every WP3 row in §10 passes.
  - The InMemory contract suites are green. The Postgres suites compile and are skipped without `TTT_TEST_PG`.
  - The migrations apply cleanly to an empty Postgres if one is available; this is optional and noted in WP3 notes.
  - The server boots with InMemory + Dev, and `GET /api/auth/dev/users` lists the 8 fixtures.
- **Test commands:** `dotnet test tests/TicTacToeUltimate.Server.Tests --filter "FullyQualifiedName~Auth|FullyQualifiedName~Persistence|FullyQualifiedName~Ratings|FullyQualifiedName~Progression|FullyQualifiedName~Social|FullyQualifiedName~Rewards|FullyQualifiedName~Api"`.

#### WP4 Bot gateway and end-to-end integration tests
- **Owns:** `TicTacToeUltimate.Server/Bots/**`, `spec/bot-protocol.md`, `tests/TicTacToeUltimate.Server.IntegrationTests/**` (incl. csproj), `docs/notes/WP4.md`.
- **Implements:** §6.9, §7.1, `IBotPool`, BotKey auth handler (scheme `"BotKey"`, policy `Bot` satisfied), `/api/bots/keys`, `AddTttBots`/`MapTttBots`, the integration suite (§10 row).
- **Consumes:** `IMatchGateway`, `IChallengeService`, `IBotKeyRepository`, `IUserRepository`, Contracts.Bot.
- **Depends on:** WP0. The bot unit tests use a fake `IMatchGateway`. The E2E suite goes green after WP1, WP2 and WP3 merge (merge order puts WP4 after them).
- **Acceptance:**
  - The integration suite passes with zero external services, in under 60 s.
  - The protocol doc matches §7.1 and the Contracts.Bot types exactly.
  - Concurrent sends to one socket are serialized (test).
- **Test commands:** `dotnet test tests/TicTacToeUltimate.Server.IntegrationTests`.

#### WP5 Client core and hosts (web + mobile)
- **Owns:**
  - `TicTacToeUltimate.Client/**` except `Abstractions/**` and `State/Contracts/**`
  - `TicTacToeUltimate.Web/**`, `TicTacToeUltimate.Web.Client/**`, `TicTacToeUltimate/**` (MAUI, incl. csproj and lock file)
  - `tests/TicTacToeUltimate.Client.Tests/**`, `docs/notes/WP5.md`
  - `README.md` (after WP0)
- **Implements:** §8.2 behaviour, §8.3 stores, `AddTttClient`, `StubAdProvider`, `AdPolicy`, `AdCoordinator`, host wiring (§8.6), MAUI platform services, manifests and plists, icon and splash colours, the run instructions in README.
- **Consumes:** Contracts (hub, REST), Engine, the `AddTttSharedUi` and `Js*` classes named in §8.4 (stubs exist from WP0).
- **Depends on:** WP0. Tests use a fake hub transport (an `IRealtimeTransport` seam internal to WP5).
- **Acceptance:**
  - Every WP5 row in §10 passes.
  - All three MAUI TFMs build.
  - The web app (after WP6 merges) plays a full game between two tabs against the local server.
  - The MAUI app on the iOS simulator, the Android emulator and MacCatalyst signs in as a dev user and plays against a web tab. Record this manual check in notes.
- **Test commands:** `dotnet test tests/TicTacToeUltimate.Client.Tests`; the three MAUI builds in §10.1; `dotnet build TicTacToeUltimate.Web.slnf`.

#### WP6 UI: design system, layouts, pages, assets
- **Owns:** `TicTacToeUltimate.Shared/**` (incl. csproj), `tests/TicTacToeUltimate.Shared.Tests/**`, `docs/notes/WP6.md`.
- **Implements:** §8.4 and §8.5. Tokens and components are ported from `/design/system`, and the pages match `/design/mockups` at 390×844 (BottomNav) and ≥900 px (Sidebar). Dark default with a light theme. Reduced motion supported. Impact frames for win, loss, sudden-death start and promotion. Synthesized SFX and heartbeat. `Js*` platform defaults, `AdOverlayService`, `AddTttSharedUi`, fake stores.
- **Consumes:** only §8.2/§8.3 interfaces, Contracts DTOs and Engine (for the read-only replay board). There is **no** direct `HubConnection`/`HttpClient` use.
- **Depends on:** WP0.
- **Acceptance:**
  - Every WP6 row in §10 passes.
  - Each mockup screen renders from fake stores with the mockup's data (fixtures named in §6.7).
  - No ads under MatchLayout.
  - Every interactive element is at least 44 px and has a focus ring (token `focus`).
- **Test commands:** `dotnet test tests/TicTacToeUltimate.Shared.Tests`; `dotnet build TicTacToeUltimate.Web.slnf`.

### 13.4 Contract change requests
If a frozen type is wrong or missing, the requesting WP adds `docs/contract-requests/WPn-<slug>.md` (owned by the requester). The file contains the exact proposed C# diff and the reason. Meanwhile, the WP works around it inside its own folder (adapter or internal type) and never edits the frozen file. At integration the lead applies the accepted requests and notifies affected WPs through their notes files. Additive changes (new optional DTO members with defaults, new DTOs) are accepted by default. Renames and removals are rejected unless a test proves the frozen shape is unusable.

### 13.5 Work package summary (machine-readable)
```json
[
  {"id":"WP0","name":"Foundation and contracts","depends":[],"owns":["global.json","Directory.Build.props","Directory.Packages.props","TicTacToeUltimate.sln","TicTacToeUltimate.Web.slnf",".gitignore","scripts/**","design/**","tools/study/**","AGENTS.md","docs/ARCHITECTURE.md","TicTacToeUltimate.Contracts/**","TicTacToeUltimate.Server/{csproj,Program.cs,appsettings*.json,Properties/**,Hosting/**,Abstractions/**,Realtime/GameHub.cs}","TicTacToeUltimate.Client/{Abstractions/**,State/Contracts/**}","tests/TicTacToeUltimate.Server.Tests/*.csproj"],"test":"scripts/build-all.sh && dotnet test TicTacToeUltimate.Web.slnf"},
  {"id":"WP1","name":"Engine, tablebase, analysis, vectors","depends":["WP0"],"owns":["TicTacToeUltimate.Engine/**","tools/TicTacToeUltimate.RulesTool/**","spec/rules/**","spec/clock/**","docs/rules.md","TicTacToeUltimate.Server/Analysis/**","tests/TicTacToeUltimate.Engine.Tests/**","tests/TicTacToeUltimate.Server.Tests/Analysis/**"],"test":"dotnet test tests/TicTacToeUltimate.Engine.Tests && dotnet run --project tools/TicTacToeUltimate.RulesTool -- gen --check"},
  {"id":"WP2","name":"Realtime match core","depends":["WP0"],"owns":["TicTacToeUltimate.Server/{Matches,Matchmaking,Challenges,Presence}/**","TicTacToeUltimate.Server/Realtime/GameHub.{Session,Queue,Match,Challenge}.cs","tests/TicTacToeUltimate.Server.Tests/{Matches,Clocks,Matchmaking,Challenges,Presence}/**"],"test":"dotnet test tests/TicTacToeUltimate.Server.Tests --filter FullyQualifiedName~Matches|FullyQualifiedName~Clocks|FullyQualifiedName~Matchmaking|FullyQualifiedName~Challenges|FullyQualifiedName~Presence"},
  {"id":"WP3","name":"Platform services (auth, persistence, ratings, progression, social, rewards, REST, Supabase)","depends":["WP0"],"owns":["TicTacToeUltimate.Server/{Auth,Persistence,Ratings,Progression,Social,Rewards,Api}/**","TicTacToeUltimate.Server/Realtime/GameHub.Social.cs","supabase/**","tests/TicTacToeUltimate.Server.Tests/{Auth,Persistence,Ratings,Progression,Social,Rewards,Api}/**"],"test":"dotnet test tests/TicTacToeUltimate.Server.Tests --filter FullyQualifiedName~Auth|FullyQualifiedName~Persistence|FullyQualifiedName~Ratings|FullyQualifiedName~Progression|FullyQualifiedName~Social|FullyQualifiedName~Rewards|FullyQualifiedName~Api"},
  {"id":"WP4","name":"Bot gateway and E2E integration tests","depends":["WP0"],"mergeAfter":["WP1","WP2","WP3"],"owns":["TicTacToeUltimate.Server/Bots/**","spec/bot-protocol.md","tests/TicTacToeUltimate.Server.IntegrationTests/**"],"test":"dotnet test tests/TicTacToeUltimate.Server.IntegrationTests"},
  {"id":"WP5","name":"Client core and hosts (web + MAUI iOS/Android/MacCatalyst)","depends":["WP0"],"owns":["TicTacToeUltimate.Client/** (except Abstractions, State/Contracts)","TicTacToeUltimate.Web/**","TicTacToeUltimate.Web.Client/**","TicTacToeUltimate/**","tests/TicTacToeUltimate.Client.Tests/**","README.md"],"test":"dotnet test tests/TicTacToeUltimate.Client.Tests && dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-android && dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64 && dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-maccatalyst"},
  {"id":"WP6","name":"UI: design system, layouts, pages, assets","depends":["WP0"],"owns":["TicTacToeUltimate.Shared/**","tests/TicTacToeUltimate.Shared.Tests/**"],"test":"dotnet test tests/TicTacToeUltimate.Shared.Tests && dotnet build TicTacToeUltimate.Web.slnf"}
]
```
Every WP also owns `docs/notes/WPn.md` and may create `docs/contract-requests/WPn-*.md`.
