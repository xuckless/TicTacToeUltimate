#!/usr/bin/env bash
# Static web build (Blazor WebAssembly, no server): output in deploy/web/out. Used by Vercel and the nginx image.
# Env: TTT_BACKEND_URL (game server, e.g. https://api.example.com), TTT_WEB_URL (this site's public URL),
#      TTT_ADSENSE_CLIENT (optional ca-pub-…), DOTNET (optional path to dotnet).
set -euo pipefail
cd "$(dirname "$0")/../.."
DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1; then
  # Vercel build images have no .NET: install the SDK pinned by global.json.
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --jsonfile global.json --install-dir "$HOME/.dotnet" >/dev/null
  DOTNET="$HOME/.dotnet/dotnet"
fi
rm -rf deploy/web/out
"$DOTNET" publish TicTacToeUltimate.Web.Client -c Release -o deploy/web/publish
mv deploy/web/publish/wwwroot deploy/web/out && rm -rf deploy/web/publish
cat > deploy/web/out/appsettings.Production.json <<JSON
{ "Backend": { "BaseUrl": "${TTT_BACKEND_URL:-http://localhost:5091}" }, "Web": { "BaseUrl": "${TTT_WEB_URL:-}" },
  "Ads": { "AdSenseClient": "${TTT_ADSENSE_CLIENT:-}" } }
JSON
echo "static site: deploy/web/out"
