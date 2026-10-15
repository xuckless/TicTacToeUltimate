# TicTacToeUltimate

Real-time PvP vanishing tic-tac-toe. Web (Blazor WASM) + .NET MAUI (iOS, Android, MacCatalyst) clients, ASP.NET Core game server.

- Product decisions: `AGENTS.md`. Implementation spec: `docs/ARCHITECTURE.md`.
- Full build/test matrix: `scripts/build-all.sh`.

## Run locally

```bash
scripts/dev.sh        # server http://localhost:5091, web http://localhost:5028
```

- Web: open http://localhost:5028. Two players on one machine: open two tabs with `?as=ali_xo` and `?as=kestrel_99`
  (Development only; the tab-only sign-in does not overwrite the other tab's saved token).
- MAUI (server must be running; backend URL comes from `TicTacToeUltimate/Resources/Raw/appsettings.json`, empty = dev default):
  - iOS simulator: `dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-ios -t:Run -p:RuntimeIdentifier=iossimulator-arm64` (uses `http://localhost:5091`)
  - Android emulator: `dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-android -t:Run` (uses `http://10.0.2.2:5091`)
  - MacCatalyst: `dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-maccatalyst -t:Run`
- Deep links: `tttu://c/{code}` opens a challenge in the app.

## Tests

```bash
dotnet test TicTacToeUltimate.Web.slnf
dotnet test tests/TicTacToeUltimate.Client.Tests
```
