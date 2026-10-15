# Post-merge integration notes (2026-10-15)

- **SignalR user id**: `AddSignalR` TryAdds `DefaultUserIdProvider` (NameIdentifier), so WP0's `TryAdd<IUserIdProvider, SubUserIdProvider>` lost and every `Clients.User(...)` push (MatchFound, DMs, notifications) went nowhere. `AddTttCore` now `Replace`s it.
- **TablebaseCache**: `Dispose` / `StopAsync` are idempotent (host disposal after stop threw `ObjectDisposedException`).
- **E2E seq streams**: per §5 join ordering each client sees events after its own snapshot Seq; the ranked E2E test compares streams from the later join on.
- **RuleIds**: WP3's `Persistence/RuleIds.cs` and the WP2 regex parser are gone; `Matches.RuleIds` is a thin wrapper over `RuleSet.TryParse`. Fixture playouts use the engine only (no local fallback).
- **blocked-words.txt** is copied to output (`Social/Data/*.txt`).
- **REST**: client calls `GET /api/live?friends=true` (spec §6.5) instead of `/api/friends/live`. `/api/challenges/{code}` and `/api/daily` were already served by WP2.
- **Fonts**: no `.woff2` bundled (WP6); preload links removed from `Web/Components/App.razor`, token fallback stacks apply.
- Hosts (Web, Web.Client, MAUI) already register WP5 real stores (`AddTttClient`) + WP6 `Js*` classes (`AddTttSharedUi`) + Shared `Routes` assembly + the four CSS files.
