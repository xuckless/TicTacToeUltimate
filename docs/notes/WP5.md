# WP5 notes (client core + hosts)

- Test seam: `IRealtimeTransport` (public, `Client/Services`) = `IGameHub` + typed push events + `Handshake`/`Connected`. `RealtimeClient` is the only `HubConnection`.
- Platform fallbacks: `AddTttClientFallbacks()` (TryAdd in-memory/no-op `IKeyValueStore`, `ISecureStore`, `IHaptics`, `ISoundService`, `IAppLifecycle`, `IAdOverlay`, ...). Hosts call it **last**, after `AddTttSharedUi()` and their overrides, so Shared's `TryAdd` JS defaults still win. Lets the app run before WP6 lands.
- Engine use is defensive: `MatchStore` catches engine exceptions. If `Rules.Replay` fails, `MatchView.Engine` is null (keeps last good) and placement validation falls back to `Snapshot.LegalCells`. Tests pass with the WP0 engine stubs and with WP1's engine.
- REST endpoints not in §6.7 that the client calls (404 → null/empty): `GET /api/challenges/{code}`, `GET /api/daily`, `GET /api/friends/live`. WP3 may add them; otherwise `GetChallengeAsync` falls back to incoming challenges.
- 401 handling: guest → re-mint by `ttt.deviceId`; otherwise sign out (`Status = SignedOut`, the UI AuthGate routes to `/signin`). Last account kind is kept in `ttt.kind` so an expired guest token at startup re-mints.
- `?as={username}` (Web.Client, Development): `SessionStore.UseDevImpersonation` signs the tab in via `/api/auth/dev/token` and keeps the token in memory only (not persisted), so other tabs are unaffected.
- AdPolicy: aborted games don't count; "skip after two losses" = no interstitial while the loss streak is ≥ 2 (deferred to the next qualifying game). Blocked (match route / queued) defers too.
- Settings JSON is hand-written (`JsonNode`), trim-safe; theme mirrored to `ttt.theme` for the pre-paint bootstrap (absent = System).
- Clock ticks: 10 Hz timer via `TimeProvider`, only while an open match has a running clock. Warning cue at 5 s, tick each second ≤ 4 s, heartbeat in sudden death on your turn.
- Web.Client keeps Shared's `JsLifecycle` (from `AddTttSharedUi`) for `IAppLifecycle`; WP0's Shared stub has no `Js*` classes yet, so until WP6 merges the fallbacks are used.
- Android `network_security_config.xml` allows cleartext only to `10.0.2.2` and `localhost` (applies to all configs; harmless in Release). AdMob manifest/plist keys removed. Phones are portrait-locked.
- Font preload paths in `Web/Components/App.razor` assume `fonts/LilitaOne-Regular.woff2` and `fonts/Nunito-700.woff2` (WP6 to confirm).
- Manual device check (iOS sim / Android emu / Catalyst vs a web tab): **not yet run** in this WP; needs WP2/WP3/WP6 merged.
