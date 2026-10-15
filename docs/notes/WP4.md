# WP4 notes: bot gateway and E2E

- `Server/Bots`: `BotKeyResolver` (dev keys from `Bots:DevKeys`, then `IBotKeyRepository` if registered), `BotKeyAuthenticationHandler` (scheme `BotKey`), `BotPool : IBotPool`, `BotSession : IMatchObserver`, `BotOutbox` (single writer per socket, close frame also goes through it), `/bot/ws`, `/api/bots/keys`.
- Dev bot user id = UUIDv5(`GuidV5.BotNamespace`, `"dev-bot:" + keyId`); `DevBotUserSeeder` creates the Bot user row when `IUserRepository` exists.
- `Bot` policy: set by `AddTttBots` (BotKey scheme + `ttt_kind=bot`), overriding any earlier definition. `Account` policy is added only if WP3 has not defined it.
- Ordering: while a bot action is in flight, that match's envelopes are buffered so the bot always sees `ack`/`error` before the resulting `state`.
- `state` is emitted on `TurnStarted` and once more (phase `Ended`) before `gameEnd`.
- `IMatchGateway`/`IChallengeService` are resolved optionally, so the gateway works before WP2 merges (actions answer `match_not_found`).
- Ping interval etc. come from an optional `BotSessionSettings` singleton (tests shorten it).
- Tests: `tests/TicTacToeUltimate.Server.IntegrationTests/Bots/**` use an in-file fake `IMatchGateway` and pass without WP1–WP3. `E2E/**` skips itself (`Assert.SkipWhen`) until a real `IMatchGateway` and dev auth exist; it uses `FakeTimeProvider` + time pumping for matchmaking.
