#!/usr/bin/env bash
# Static web build (Blazor WebAssembly, no server): output in deploy/web/out. Used by Vercel and the nginx image.
# Env: TTT_BACKEND_URL (game server, e.g. https://api.example.com), TTT_WEB_URL (this site's public URL),
#      TTT_ADSENSE_CLIENT (optional ca-pub-…), TTT_ADSENSE_BANNER_SLOT / TTT_ADSENSE_RAIL_SLOT (display and vertical
#      unit ids; default to the live units, used only when TTT_ADSENSE_CLIENT is set),
#      TTT_ADSENSE_TEST (true = AdSense test ads), DOTNET (optional path to dotnet).
set -euo pipefail
TTT_ADSENSE_BANNER_SLOT="${TTT_ADSENSE_BANNER_SLOT-6385289336}"   # "Display Square"
TTT_ADSENSE_RAIL_SLOT="${TTT_ADSENSE_RAIL_SLOT-8269216645}"       # "ads vertical"
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
# SEO pages, robots/sitemap, icons and social preview image.
cp -R deploy/web/static/. deploy/web/out/
rm -f deploy/web/out/appsettings.Development.json*
# Blazor only loads config files listed in its boot manifest, so production values replace appsettings.json itself
# (and its precompressed copies, which would be stale).
rm -f deploy/web/out/appsettings.json.br deploy/web/out/appsettings.json.gz
cat > deploy/web/out/appsettings.json <<JSON
{ "Backend": { "BaseUrl": "${TTT_BACKEND_URL:-http://localhost:5091}" }, "Web": { "BaseUrl": "${TTT_WEB_URL:-}" },
  "Ads": { "AdSenseClient": "${TTT_ADSENSE_CLIENT:-}", "AdSenseBannerSlot": "${TTT_ADSENSE_BANNER_SLOT:-}", "AdSenseRailSlot": "${TTT_ADSENSE_RAIL_SLOT:-}", "TestMode": "${TTT_ADSENSE_TEST:-false}" } }
JSON
# AdSense requires /ads.txt naming the publisher.
if [ -n "${TTT_ADSENSE_CLIENT:-}" ]; then
  echo "google.com, ${TTT_ADSENSE_CLIENT#ca-}, DIRECT, f08c47fec0942fa0" > deploy/web/out/ads.txt
  # The AdSense tag in every page's static <head>, so the site review crawler sees it (ads.js reuses it, never loads twice).
  test_attr=""; [ "${TTT_ADSENSE_TEST:-false}" = "true" ] && test_attr=' data-adbreak-test="on"'
  tag="<script async src=\"https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=${TTT_ADSENSE_CLIENT}\" crossorigin=\"anonymous\"${test_attr}></script>"
  find deploy/web/out -maxdepth 2 -name index.html | while read -r f; do
    TAG="$tag" perl -0pi -e 's#</head>#$ENV{TAG}\n</head>#' "$f"
    rm -f "$f.br" "$f.gz"   # precompressed copies would be stale
  done
fi
echo "static site: deploy/web/out"
