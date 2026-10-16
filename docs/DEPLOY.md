# Deploying Tic Tac Toe Ultimate

Production: **https://tictactoeultimate.alidirect.dev**

```
browser / app ──https──> Cloudflare Worker "tictactoeultimate-web" (deploy/cloudflare)
                           ├─ static assets: deploy/web/out (Blazor WASM + SEO pages)
                           └─ /api /hubs /bot /health ──https + X-Origin-Auth──> VM origin (Caddy)
GCE e2-micro VM (deploy/vm/docker-compose.yml): caddy ─> server:8080 ─(dispatch)─> bots:8080 ─(ws)─> server
server ──> Supabase Postgres (session pooler) + Supabase Auth (JWKS)
```

- **Web**: static files on Cloudflare (free). The Worker proxies the game API to the VM, so the site and the API share one origin (no CORS) and the VM's IP stays hidden. `alidirect.dev` (apex) is a second Worker that serves `ads.txt` and redirects to the game.
- **Server and bots**: one always-on e2-micro (free tier: us-east1, 30 GB standard disk) running Docker Compose. Every container restarts on crash and on boot. Caddy has automatic HTTPS on an sslip.io name and rejects any request without the Worker's secret header.
- **Cold start**: the server needs about 25 s to build its tablebases. The web app shows a "waking up the game server" buffer while it retries for up to 60 s.
- **Database/auth**: Supabase project `tictactoeultimate` (migrations in `supabase/migrations`, auth config in `supabase/config.toml`; push with `supabase db push` / `supabase config push`).

## Release steps
1. `docker buildx build --platform linux/amd64 -f TicTacToeUltimate.Server/Dockerfile -t ttt-server:amd64 --load .` (and `bots/Dockerfile` → `ttt-bots:amd64`).
2. `docker save ttt-server:amd64 ttt-bots:amd64 | gzip -1 > images.tar.gz`, `gcloud compute scp` it to the VM, then `docker load` and `docker compose up -d` in `/opt/ttt`.
3. Web: `TTT_BACKEND_URL=… TTT_WEB_URL=… TTT_ADSENSE_CLIENT=ca-pub-… deploy/web/build.sh`, then `wrangler deploy -c deploy/cloudflare/wrangler.jsonc`.

The secrets (DB connection string, guest-token signing key, bot keys, dispatch token, origin secret) live only on the VM (`/opt/ttt/*.env`, `personas.json`) and in Worker secrets. They are never committed.

## Ads
- Web: AdSense (`ca-pub-7292645320711380`), with `ads.txt` on the apex and on the game subdomain. It uses the Ad Placement API (H5 Games Ads) for interstitials and rewarded ads. A banner unit can be added later through `TTT_ADSENSE_BANNER_SLOT`.
- Mobile: AdMob (see `DEPLOYMENT_GUIDE.md`, local only).
