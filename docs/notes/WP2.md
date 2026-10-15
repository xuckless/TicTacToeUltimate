# WP2 notes — realtime match core

- **Engine seam.** The actor talks to the engine through `IGameRules` / `IClockSchedule` (`Matches/GameRulesPort.cs`); production adapters `EngineGameRules` / `EngineClockSchedule` call WP1's `Rules`/`MoveClock`. Tests use the reference fakes in `tests/.../Matches/TestKit.cs` (`RefRules`, `RefClock`), so WP2 tests pass independent of WP1. `RuleIds.Parse` is a local id parser (no dependency on `RuleSet.TryParse`).
- **Actor.** `MatchActor`: single-reader inbox, ingress lock is the only lock, `Handle` is synchronous; Daily persistence (`AppendActionAsync`) is awaited between `Handle` and the reply. Outbox publisher does group adds, group sends, observer callbacks, the result sink, and notifications in order.
- **Delivery seam.** `IMatchBroadcaster` (prod: `HubMatchBroadcaster`, groups `m:{matchId:N}`).
- **Optional dependencies.** WP3 repositories (`IDailyMatchRepository`, `IChallengeRepository`, `IUserRepository`, `IRatingRepository`, `IFriendRepository`) are resolved with `GetService` through factories, so the server boots before WP3 merges. Without a daily repo, Daily matches are in-memory only. Without a friend repo, spectating is refused.
- **Challenges.** Accept is CAS-first (first acceptor wins), then the live-activity check; if the activity check fails, the CAS is reverted to Open and the caller gets `already_in_match`. The 5-minute expiry sweep covers codes created by this process (the repository has no "list open" query).
- **Daily limit** uses `IDailyMatchRepository.ListActiveForUserAsync` when it is registered; otherwise it falls back to an in-memory count.
- **Abort reasons** go in `MatchResultDto.Detail`: `timeout`, `resign`, `abandoned`, `server_restart`.
- **Deviation:** `RttSample` does not emit an event. The latest RTT appears in `SeatDto.RttMs` on snapshots, connection-change events and ActionApplied seats.
