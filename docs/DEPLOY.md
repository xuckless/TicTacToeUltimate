# Deploying Tic Tac Toe Ultimate

| Piece | Where | Why |
|---|---|---|
| Web (Blazor WebAssembly, static) | **Vercel** | `vercel.json` + `deploy/web/build.sh`. The site is plain static files, so Vercel's CDN is ideal. |
| Game server (ASP.NET Core + SignalR) | **Google Cloud Run** (1 instance) *or* your workstation behind Tailscale Funnel | Needs long-lived WebSockets and in-memory match actors. Vercel Functions can't do either and have no .NET runtime. |
| Bot service (`bots/`, Go) | Same place as the server (recommended), or your workstation via Docker + Tailscale | The server must reach `POST /v1/sessions`; the bots must reach the server's `/bot/ws`. |
| Database + auth | **Supabase** `tictactoe-ultimate` (ref `mmoxnxiiqftnfqxqueob`, us-east-1) | Migrations in `supabase/migrations`; `supabase db push`. |

Measured locally: the game server is about 110 MB RSS idle and botd about 11 MB, so a 512 MiB Cloud Run instance is plenty.

## 1. Supabase (done)
- Schema pushed (`supabase db push`). RLS is on for every table and there are no anon policies: only the server's DB connection reads and writes.
- Auth: email + password only, confirmations on, anonymous sign-ins off. Guests are server-issued device tokens, not Supabase users.
  - When a guest signs in, `POST /api/auth/claim-guest` moves their games (replays), streak, quests and hidden casual ratings into the account.
- **Before launch:** in Auth → URL configuration, set Site URL to your production web URL. In Auth → SMTP, add a real SMTP provider (Resend, Postmark…): Supabase's built-in mailer only sends a few emails per hour.

## 2. Game server: Cloud Run
```bash
gcloud run deploy ttt-server --image ghcr.io/<owner>/ttt-server:latest --region us-east1 \
  --min-instances 1 --max-instances 1 --session-affinity --timeout 3600 --cpu 1 --memory 512Mi --no-cpu-throttling \
  --set-env-vars "Ttt__Web__BaseUrl=https://<web>,Ttt__Cors__Origins__0=https://<web>,Ttt__Bots__Service__Url=https://<botd>,Ttt__Ads__Provider=AdSense" \
  --set-secrets "Ttt__Persistence__Postgres__ConnectionString=ttt-pg:latest,Ttt__Auth__Dev__SigningKey=ttt-guest-key:latest,Ttt__Bots__Service__Token=ttt-dispatch:latest"
```
- **One instance only**: matchmaking and live matches are in memory (single node by design), so keep max = min = 1.
- `ttt-pg`: `Host=aws-0-us-east-1.pooler.supabase.com;Port=5432;Username=postgres.mmoxnxiiqftnfqxqueob;Password=…;Database=postgres;SSL Mode=Require`. This is the session pooler; it is IPv4-friendly.
- `ttt-guest-key`: any 64-character random string (`openssl rand -hex 32`). It signs guest tokens. Keep it stable, or every guest gets re-minted after a deploy.
- Bot personas (one bot account per human-like name): `Ttt__Bots__DevKeys__0__Name` / `__Key`, … The same list goes to botd as JSON (see below). Generate fresh keys for production (`tttb_<id>_<secret>`).

## 3. Bot service
- Cloud Run: image `ghcr.io/<owner>/ttt-bots:latest`, port 8080, env `TTT_SERVER_WS=wss://<server>/bot/ws?protocol=1`, `TTT_DISPATCH_TOKEN` (same as the server's), personas mounted as a secret file → `TTT_BOT_PERSONAS=/secrets/personas.json`. min-instances 0 is fine: a dispatch wakes it.
- Workstation: `docker run -d --restart unless-stopped -p 7070:8080 -e TTT_SERVER_WS=… -e TTT_DISPATCH_TOKEN=… -v $PWD/personas.json:/p.json -e TTT_BOT_PERSONAS=/p.json ghcr.io/<owner>/ttt-bots`.
  - Then `tailscale funnel 7070` gives a public `https://<machine>.<tailnet>.ts.net` URL for the server's `Ttt__Bots__Service__Url`.
  - Plain Tailscale (no Funnel) only works if the game server is on the tailnet too.

## 4. Web: Vercel
- Import the GitHub repo in Vercel (no API key needed; the Git integration builds on every push). Framework preset: **Other**. Settings come from `vercel.json`.
- Project env vars:
  - `TTT_BACKEND_URL=https://<server>`
  - `TTT_WEB_URL=https://<web>`
  - optional: `TTT_ADSENSE_CLIENT=ca-pub-…`, `TTT_ADSENSE_BANNER_SLOT=…`, `TTT_ADSENSE_TEST=true` while testing.
- The build installs the .NET SDK (pinned by `global.json`) on Vercel's image. The first build takes a few minutes.

## 5. Ads (web)
- AdSense account → add the site → apply for **H5 Games Ads** (Ad Placement API). That covers interstitials between games and rewarded ads (streak freeze, extra quest, analysis).
- Create one display ad unit for the menu banner (its slot id → `TTT_ADSENSE_BANNER_SLOT`).
- `build.sh` writes `ads.txt` from `TTT_ADSENSE_CLIENT`.
- Ads never show during a match. With no fill or an ad blocker, everything degrades to "no ad" within 5 s.
- Rewarded receipts on web are one-time nonces (`web:{kind}:{uuid}`, dedup'd by the server ledger). The Ad Placement API has no server-side verification. Mobile will use AdMob server-side verification later.

## Keys you still need to provide
- **AdSense publisher id** (`ca-pub-…`) and a banner slot id, once AdSense approves the site.
- **GCP project** (if Cloud Run) with Artifact/GHCR pull access, or the workstation + Tailscale.
- **SMTP provider** credentials for Supabase auth emails (production volume).
- Nothing else: the Supabase URL and publishable key are public and committed in `appsettings.Production.json`. The DB password stays in `.env` and in the host's secret store.
