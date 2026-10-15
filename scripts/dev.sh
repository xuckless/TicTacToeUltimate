#!/usr/bin/env bash
# Starts the game server (5091) and the web host (5028) in parallel. Ctrl+C stops both.
set -euo pipefail
cd "$(dirname "$0")/.."
trap 'kill 0' EXIT INT TERM
ASPNETCORE_ENVIRONMENT=Development dotnet run --project TicTacToeUltimate.Server --no-launch-profile --urls http://localhost:5091 &
ASPNETCORE_ENVIRONMENT=Development dotnet run --project TicTacToeUltimate.Web --no-launch-profile --urls http://localhost:5028 &
echo "Server: http://localhost:5091  (health: http://localhost:5091/health)"
echo "Web:    http://localhost:5028"
wait
