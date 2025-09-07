# © 2025 Xuckless. All Rights Reserved. This software may not be used, copied, modified, or distributed without prior written consent.

# TicTacToeUltimate

Multiplayer Tic-Tac-Toe with MAUI (mobile + hybrid), Blazor (web), ASP.NET Core backend, PostgreSQL, Redis, SignalR, and Stripe (Apple Pay + Google Pay).

---

## Tech Stack
- **Frontend**
  - .NET MAUI (CommunityToolkit.Mvvm)
  - Blazor Web (WASM/Server)
- **Backend**
  - ASP.NET Core Minimal API
  - SignalR (Realtime)
  - Entity Framework Core (PostgreSQL with Npgsql)
  - Redis (match state, lobbies, caching)
  - ASP.NET Identity + JWT + Google/Apple external auth
  - Stripe.NET (Apple Pay + Google Pay)
- **Hosting**
  - Azure App Service
  - Azure Database for PostgreSQL
  - Azure Cache for Redis
- **CI/CD**
  - GitHub Actions
- **IDE**
  - JetBrains Rider

---

## Dependencies

**NuGet packages**
- Microsoft.EntityFrameworkCore
- Npgsql.EntityFrameworkCore.PostgreSQL
- StackExchange.Redis
- Microsoft.AspNetCore.SignalR
- Microsoft.AspNetCore.SignalR.Client
- Microsoft.AspNetCore.Authentication.JwtBearer
- Microsoft.AspNetCore.Authentication.Google
- AspNet.Security.OAuth.Apple
- Stripe.net
- Swashbuckle.AspNetCore (Swagger)
- CommunityToolkit.Mvvm
- CommunityToolkit.Maui

**External**
- Docker Desktop (for Redis)
- PostgreSQL (local or cloud)
- Stripe account
- Apple Developer + Google Developer accounts

---

## Configuration

### Auth
Commit this sample file: BackendWebAPI/appsettings.Auth.json ( add it to git ignore later )

Developers copy → `appsettings.Auth.json` (gitignored) and fill:

```json
{
  "Auth": {
    "Google": { "ClientId": "xxx", "ClientSecret": "xxx" },
    "Apple":  { "ClientId": "com.bundleid", "TeamId": "TEAMID", "KeyId": "KEYID", "PrivateKey": "PEM" }
  }
}
```


### Stripe

Commit this sample file: BackendWebAPI/appsettings.Stripe.json
Developers copy → `appsettings.Stripe.json` (gitignored) and fill:
```
{
  "Stripe": {
    "SecretKey": "sk_test_xxx",
    "PublishableKey": "pk_test_xxx",
    "WebhookSecret": "whsec_xxx",
    "Currency": "usd"
  }
}
```

### Database

In `appsettings.json`:
```
"ConnectionStrings": {
  "Postgres": "Host=localhost;Database=tictactoe;Username=app;Password=secret"
}
```

### Redis

Run in docker
```
docker run -d --name redis -p 6379:6379 redis:7
```
In `appsettings.json`:

```
"Redis": "localhost:6379"
```


## Run Instructions

### Backend (Mac/Windows)
	1.	Open TicTacToeUltimate.sln in Rider.
	2.	Use Compound run config API + Redis (starts Redis and BackendWebAPI).
	3.	Run. Swagger available at https://localhost:<port>/swagger.
	4.	Test API: `curl http(s)://localhost:<port>/ping`

### Web
	1.	Run TicTacToeUltimate.Web (Blazor).
	2.	Ensure API CORS allows Blazor origin (Cors:AllowedOrigins in appsettings.json).

### Mobile
	1.	Install workloads: `dotnet workload install maui-android maui-io`

	2.	Rider run configs:
	•	MAUI Android → target emulator/device.
	•	MAUI iOS → requires Mac/iPhone + provisioning.


## Git Hygiene

Add a .gitignore at the repo root:

```
# Build
bin/
obj/

# IDE
.idea/

# User-specific
*.user
*.suo
*.userosscache
*.userprefs

# Secrets
BackendWebAPI/appsettings.Auth.json
BackendWebAPI/appsettings.Stripe.json

# OS junk
.DS_Store
Thumbs.db
desktop.ini
```


# ❌ Never commit real secrets (appsettings.Auth.json, appsettings.Stripe.json).


