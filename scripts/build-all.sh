#!/usr/bin/env bash
# Full build/test matrix (ARCHITECTURE.md §10.1). Exits non-zero on the first failure; no skips.
set -euo pipefail
cd "$(dirname "$0")/.."
run() { echo "==> $*"; "$@"; }
run dotnet build TicTacToeUltimate.Web.slnf
run dotnet test  TicTacToeUltimate.Web.slnf --no-build
run dotnet run --project tools/TicTacToeUltimate.RulesTool -- gen --check
run dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-android
run dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
run dotnet build TicTacToeUltimate/TicTacToeUltimate.csproj -f net10.0-maccatalyst
echo "All green."
