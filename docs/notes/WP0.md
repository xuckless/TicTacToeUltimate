# WP0 notes

- Package pins (2026-10-15): xunit.v3 4.0.2, xunit.runner.visualstudio 4.0.1, Microsoft.NET.Test.Sdk 18.11.0, bunit 2.11.3, Npgsql 10.0.3, Dapper 2.1.89, Microsoft.Extensions.TimeProvider.Testing 10.11.0; ASP.NET/Extensions 10.0.13.
- MAUI packages: `PackageVersion` entries are conditional on `$(MauiVersion)`; the MAUI csproj also uses `VersionOverride="$(MauiVersion)"`.
- Engine: `RuleSet.Id` and `SplitMix64` are implemented (trivial); all other logic throws `NotImplementedException` for WP1. `GameState` has an `internal` constructor (WP1 may change it).
- `ClaimsPrincipalExtensions.ToTttUser` reads `sub` (or `ClaimTypes.NameIdentifier` when inbound claim mapping is on), `ttt_kind` (default Account), `name` (default "Player").
- `AddTttCore` inserts `ContractsJsonContext.Default` at chain position 0 and adds a reflection fallback resolver only when the chain was otherwise empty (SignalR), so non-contract payloads still serialize.
- MAUI reads `Backend:BaseUrl` from `Resources/Raw/appsettings.json`, embedded as manifest resource `appsettings.json` (no sync-over-async); empty = platform default.
- `BackendWebAPI/` and `db/` were already untracked; removed from disk and from the solution.
- `scripts/build-all.sh` runs `gen --check`, which fails until WP1 lands (expected per WP0 acceptance).
