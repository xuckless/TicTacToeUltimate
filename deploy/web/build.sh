#!/usr/bin/env bash
# Static web build (Blazor WebAssembly, no server): output in deploy/web/out. Used by Vercel and the nginx image.
# Env: TTT_BACKEND_URL (game server, e.g. https://api.example.com), TTT_WEB_URL (this site's public URL),
#      TTT_ADSENSE_CLIENT (optional ca-pub-…), TTT_ADSENSE_BANNER_SLOT (optional display unit id),
#      TTT_ADSENSE_TEST (true = AdSense test ads), DOTNET (optional path to dotnet).
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
  "Ads": { "AdSenseClient": "${TTT_ADSENSE_CLIENT:-}", "AdSenseBannerSlot": "${TTT_ADSENSE_BANNER_SLOT:-}", "TestMode": "${TTT_ADSENSE_TEST:-false}" } }
JSON
# AdSense requires /ads.txt naming the publisher.
if [ -n "${TTT_ADSENSE_CLIENT:-}" ]; then
  echo "google.com, ${TTT_ADSENSE_CLIENT#ca-}, DIRECT, f08c47fec0942fa0" > deploy/web/out/ads.txt
fi
echo "static site: deploy/web/out"
