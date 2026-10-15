# WP3 notes — Platform services

Branch `rebuild/wp3-platform` (from `rebuild/pvp-foundation`, WP0 commit 1c4dad8).

## Done
- **Auth** (`Server/Auth`): schemes `Ttt` (policy, default; forwards by unvalidated `iss`), `Dev` (HS256, `ttt-dev`/`ttt`), `Supabase` (only when enabled; authority `{Url}/auth/v1`, optional HS256 `JwtSecret`). `access_token` query read on `/hubs`. Policies `Account`/`Bot`. `TttClaimsTransformation` creates Supabase players on first sight and refreshes `ttt_kind`/`name` from the store on every request, so a guest upgraded via `PUT /api/me/username` keeps its token and id. Dev+Production throws inside `AddTttPlatform`.
- **Persistence**: every §6.3 repository in InMemory and Postgres (Npgsql + Dapper). Shared abstract contract suites in `tests/.../Persistence/Contracts`; Postgres variants skip unless `TTT_TEST_PG` points at a migrated DB.
- **Ratings**: pure `Glicko2` (paper vector test), idle RD inflation, `TierCalculator`, `RatingService`, `SeasonService` (hourly; resets only Master).
- **Progression**: `StreakRules` (local-day, freezes, cap 2, quest freeze award, bonus quest), `ProgressService`, `PostGameProcessor` (channel consumer + serialized `ProcessAsync`; `SaveCompletedAsync` is the idempotency gate).
- **Social**: `PlayerCardService` (rating/tier only for visible pools), `PresenceSink`, `SocialService` (friends, blocks, DMs: mutual friends only, 1..500 chars, masked profanity, token bucket 1/s burst 5), `GameHub.Social.cs`.
- **Rewards**: `StubRewardVerifier` + `RewardService`.
- **REST**: full §6.7 table in `Api/PlatformEndpoints.cs`. Errors are ProblemDetails with `code`.
- **Supabase**: `supabase/config.toml`, migrations `…01_init` … `…05_rls`, `seed.sql` (fixtures minus games). Migrations not applied to a live Postgres (none available here).
- **Fixtures**: `Persistence/InMemory/DevFixtures.cs`, seeded by `FixtureSeeder` (InMemory only).

## Deviations / notes for integration
- `RuleSet.TryParse` is still a WP0 stub, so WP3 uses its own `Persistence/RuleIds` parser. Fixture playouts use the engine and fall back to a local §4.2 simulation while the engine throws `NotImplementedException`.
- Postgres `games.rules` jsonb stores `{rules, seatA, seatB, rematchOf, challengeCode}` so seat names round-trip without schema changes.
- `Social/Data/blocked-words.txt` is not copied to output (csproj is WP0-owned); `ProfanityFilter` falls back to an identical built-in list. Contract request candidate: `<None Update="Social/Data/*.txt" CopyToOutputDirectory="PreserveNewest" />`.
- Bot principals (`ttt_kind=bot`, WP4) are left untouched by the claims transformation. The `Ttt` selector forwards only to Dev/Supabase; WP4 authenticates bots with its own `BotKey` scheme explicitly.
- `IPresenceTracker` is optional (`PresenceLookup`): Offline until WP2 registers it.
