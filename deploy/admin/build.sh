#!/usr/bin/env bash
# Admin site build (Blazor WebAssembly, static): output in deploy/admin/out, served by deploy/cloudflare/admin.
# The site calls /api/admin on its own origin; the admin worker proxies that to the game server. Cloudflare Access must
# protect admin.alidirect.dev (Zero Trust → Access → Applications) before the worker is deployed.
set -euo pipefail
cd "$(dirname "$0")/../.."
DOTNET="${DOTNET:-dotnet}"
rm -rf deploy/admin/out deploy/admin/publish
"$DOTNET" publish TicTacToeUltimate.Admin -c Release -o deploy/admin/publish
mv deploy/admin/publish/wwwroot deploy/admin/out && rm -rf deploy/admin/publish
rm -f deploy/admin/out/appsettings.Development.json* deploy/admin/out/appsettings.json.br deploy/admin/out/appsettings.json.gz
cat > deploy/admin/out/appsettings.json <<JSON
{ "Api": { "BaseUrl": "", "DevEmail": "" }, "Game": { "BaseUrl": "${TTT_WEB_URL:-https://tictactoeultimate.alidirect.dev}" } }
JSON
echo "admin site: deploy/admin/out"
