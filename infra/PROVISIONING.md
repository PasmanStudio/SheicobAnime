# Phase 7 — Production Provisioning Guide

> All credentials go in a password manager. NEVER commit secrets to the repo.
> Phase 9 Issues: #55 (Supabase), #56 (Upstash), #57 (Railway API), #58 (Railway Scraper), #59 (Vercel), #60 (Cloudflare)

---

## 1. Supabase PostgreSQL (Issue #38)

### Steps
1. Go to [supabase.com](https://supabase.com) → New Project
2. Region: closest to target audience (e.g. `us-east-1`)
3. Database password: generate a strong random password
4. Copy **two** connection strings:
   - **Session mode** (PgBouncer, port `6543`): used by the API at runtime + Hangfire
   - **Direct** (port `5432`): used ONLY for EF Core migrations
5. Run migrations from local machine:
   ```bash
   cd api
   dotnet ef database update --project AnimeIndex.Api \
     --connection "Host=<host>;Port=5432;Database=postgres;Username=postgres;Password=<pw>;SSL Mode=Require;Trust Server Certificate=true"
   ```
6. Verify tables created:
   ```sql
   SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';
   -- Expected: series, episodes, mirrors, genres, series_genres, blocked_slugs, scrape_jobs + Hangfire tables
   ```

### Environment variables
```
DATABASE_URL=Host=<host>;Port=6543;Database=postgres;Username=postgres;Password=<pw>;SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=10
DATABASE_URL_DIRECT=Host=<host>;Port=5432;Database=postgres;Username=postgres;Password=<pw>;SSL Mode=Require;Trust Server Certificate=true
```

---

## 2. Upstash Redis (Issue #38)

### Steps
1. Go to [upstash.com](https://console.upstash.com) → Create Database
2. Region: same as Supabase for lowest latency
3. Copy the **StackExchange.Redis** compatible endpoint (not the REST URL):
   - Format: `<host>:6379,password=<token>,ssl=True,abortConnect=False`
4. Test from local:
   ```bash
   redis-cli -h <host> -p 6379 -a <token> --tls PING
   # Expected: PONG
   ```

### Environment variable
```
REDIS_URL=<host>:6379,password=<token>,ssl=True,abortConnect=False
```

---

## 3. Railway API + Scraper (Issue #39)

### Steps
1. Go to [railway.app](https://railway.app) → New Project → Deploy from GitHub repo
2. **API service**: root directory = `/api`, Dockerfile at `api/Dockerfile`
3. **Scraper service**: root directory = `/scraper`, Dockerfile at `scraper/Dockerfile`
4. Set environment variables for API service:
   ```
   DATABASE_URL=<from Supabase Session mode>
   DATABASE_URL_DIRECT=<from Supabase Direct>
   REDIS_URL=<from Upstash>
   ADMIN_API_KEY=<generate strong random>
   HANGFIRE_DASHBOARD_PASSWORD=<generate strong random>
   RESEND_API_KEY=<from resend.com>
   SENTRY_DSN=<from Sentry>
   CORS_ORIGINS=https://<your-vercel-domain>.vercel.app,https://<custom-domain>
   ASPNETCORE_ENVIRONMENT=Production
   ```
5. Set environment variables for Scraper service:
   ```
   DATABASE_URL=<from Supabase Session mode>
   REDIS_URL=<from Upstash>
   SENTRY_DSN=<from Sentry>
   RESEND_API_KEY=<from resend.com>
   ```
6. Verify: `curl https://<railway-api-url>/health`
   - Expected: `{"status":"healthy","db":"ok","cache":"ok"}`

### Railway token for CI/CD
1. Railway → Account Settings → Tokens → Create token
2. Add to GitHub repo secrets:
   - `RAILWAY_TOKEN`
   - `RAILWAY_SERVICE_ID` (API service ID)
   - `RAILWAY_SCRAPER_SERVICE_ID` (Scraper service ID)

---

## 4. Vercel Frontend (Issue #40)

### Steps
1. Go to [vercel.com](https://vercel.com) → Import Git Repository
2. Framework: Next.js (auto-detected)
3. Root directory: `web`
4. Set environment variables:
   ```
   NEXT_PUBLIC_API_URL=https://<railway-api-url>
   NEXT_PUBLIC_AD_PROVIDER=stub
   NEXT_PUBLIC_COMMENT_PROVIDER=disqus
   NEXT_PUBLIC_DISQUS_SHORTNAME=<your-shortname>
   ```
5. Deploy and verify SSR:
   ```bash
   curl -s https://<vercel-url> | grep '<meta property="og:title"'
   ```

### Vercel tokens for CI/CD
1. Vercel → Settings → Tokens → Create
2. Add to GitHub repo secrets:
   - `VERCEL_TOKEN`
   - `VERCEL_ORG_ID`
   - `VERCEL_PROJECT_ID`

---

## 5. Cloudflare (Issue #41)

### Steps
1. Add site to Cloudflare → Update nameservers at domain registrar
2. Enable proxy (orange cloud) for A/CNAME records
3. **SSL/TLS**: Full (strict), min TLS 1.2, HSTS on
4. **Security**:
   - Bot Fight Mode: ON
   - WAF custom rule: block User-Agents matching `sqlmap|nikto|nmap|masscan|ZmEu`
5. **Cache rules**:
   - `/api/series/*` and `/api/genres` → Edge TTL 5 min
   - Everything else: respect origin headers
6. **Rate limiting** (free tier):
   - `/api/*` → 100 requests per 10 seconds per IP → challenge or block
7. Deploy keep-alive Worker (see `infra/cloudflare-worker-keepalive.js`)

---

## 6. Sentry (Issue #43)

### Steps
1. Go to [sentry.io](https://sentry.io) → Create project → ASP.NET Core
2. Copy DSN
3. Set `SENTRY_DSN` env var on Railway
4. Test: trigger a 500 error → check Sentry dashboard

---

## 7. Better Uptime (Issue #43)

### Steps
1. Go to [betteruptime.com](https://betteruptime.com) → Add monitors:
   - API health: `GET https://<railway-url>/health` every 3 min
   - Frontend: `GET https://<vercel-url>` every 5 min
2. Set up alert contacts (email/Slack)

---

## 8. GitHub Secrets Summary

| Secret | Source |
|--------|--------|
| `SUPABASE_DB_URL_DIRECT` | Supabase Direct connection string |
| `RAILWAY_TOKEN` | Railway account token |
| `RAILWAY_SERVICE_ID` | Railway API service ID |
| `RAILWAY_SCRAPER_SERVICE_ID` | Railway Scraper service ID |
| `VERCEL_TOKEN` | Vercel account token |
| `VERCEL_ORG_ID` | Vercel org/team ID |
| `VERCEL_PROJECT_ID` | Vercel project ID |

---

## 9. Post-Deploy Validation

After completing all provisioning steps above, run the smoke test:

```powershell
.\infra\smoke-test.ps1 -ApiUrl "https://api.<domain>" -WebUrl "https://<domain>" -AdminKey "<key>"
```

### Manual verification checklist

- [ ] **#55**: `curl https://api.<domain>/health` returns `{"status":"healthy","db":"ok","cache":"ok"}`
- [ ] **#55**: Supabase dashboard shows tables: series, episodes, mirrors, genres, series_genres, blocked_slugs, scrape_jobs
- [ ] **#56**: Redis PING returns PONG (test via Upstash console)
- [ ] **#57**: Railway API logs show successful startup (no missing env var errors)
- [ ] **#57**: Rate limiting works: 61st request in 1 minute returns HTTP 429
- [ ] **#58**: Railway Scraper logs show Hangfire server started with 2 workers
- [ ] **#58**: Hangfire recurring jobs visible in `/hangfire` dashboard
- [ ] **#59**: `curl -s https://<domain> | Select-String "og:title"` returns metadata
- [ ] **#59**: CORS works: frontend can fetch `/series` from API without errors
- [ ] **#60**: `curl -I https://<domain>` shows `cf-cache-status` header (Cloudflare active)
- [ ] **#60**: Direct Railway URL is not accessible (Cloudflare blocks direct IP)
- [ ] **#60**: WAF blocks `curl -A "sqlmap" https://api.<domain>/health`

### Railway-specific notes

- **API service**: Root directory = `api/`, Dockerfile at `api/Dockerfile`
- **Scraper service**: Root directory = `/` (repo root), Dockerfile at `scraper/Dockerfile`
  - The scraper needs repo root because its Dockerfile copies both `api/` and `scraper/` directories
- **Free tier**: API auto-sleeps after inactivity. Deploy the Cloudflare keep-alive worker to prevent this.
- **PgBouncer**: Use Session mode connection string for both API and Hangfire (not Transaction mode)

### Rollback plan

If a deployment fails:
1. Railway: click "Rollback" on the failed deployment in Railway dashboard
2. Vercel: click "Promote to Production" on the previous deployment
3. Database: no automatic rollback — keep a backup before running migrations
