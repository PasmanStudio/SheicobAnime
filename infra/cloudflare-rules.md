# Cloudflare Configuration Reference

> Apply these settings manually in the Cloudflare dashboard after adding the domain.
> This file is a reference — NOT an executable config.

---

## DNS Records

| Type  | Name              | Content                              | Proxy |
|-------|-------------------|--------------------------------------|-------|
| CNAME | `@`               | `cname.vercel-dns.com`               | ON    |
| CNAME | `www`             | `cname.vercel-dns.com`               | ON    |
| CNAME | `api`             | `<railway-service>.up.railway.app`   | ON    |

> Replace `<railway-service>` with the actual Railway public domain.

---

## SSL/TLS

- Mode: **Full (Strict)**
- Minimum TLS Version: **1.2**
- Always Use HTTPS: **ON**
- HSTS: **ON** (max-age 6 months, include subdomains)
- Opportunistic Encryption: **ON**

---

## Security Settings

- **Bot Fight Mode**: ON
- **Browser Integrity Check**: ON
- **Challenge Passage**: 30 minutes
- **Security Level**: Medium

---

## WAF Custom Rules

### Rule 1: Block known scanners
- **Expression**: `(http.user_agent contains "sqlmap") or (http.user_agent contains "nikto") or (http.user_agent contains "nmap") or (http.user_agent contains "masscan") or (http.user_agent contains "ZmEu") or (http.user_agent contains "dirbuster")`
- **Action**: Block

### Rule 2: Rate limit API
- **Expression**: `(http.request.uri.path matches "^/api/.*") or (http.request.uri.path matches "^/(series|genres|episodes|mirrors|admin|health)")`
- **Characteristics**: IP
- **Rate**: 100 requests per 10 seconds
- **Action**: Challenge (CAPTCHA)

### Rule 3: Block direct IP access
- **Expression**: `(http.host eq "<railway-ip>")`
- **Action**: Block

---

## Cache Rules

### Rule 1: Cache API lists (short TTL)
- **Expression**: `(http.request.uri.path matches "^/(series|genres)$")`
- **Edge TTL**: 5 minutes
- **Browser TTL**: 2 minutes
- **Cache Level**: Cache Everything

### Rule 2: Static assets (long TTL)
- **Expression**: `(http.request.uri.path matches "^/_next/static/.*")`
- **Edge TTL**: 30 days
- **Browser TTL**: 30 days

### Rule 3: Bypass cache for admin/hangfire
- **Expression**: `(http.request.uri.path matches "^/(admin|hangfire)/.*")`
- **Cache Level**: Bypass

---

## Page Rules (if needed over Cache Rules)

1. `*domain.com/api/series*` → Cache Level: Cache Everything, Edge TTL: 5 min
2. `*domain.com/admin/*` → Cache Level: Bypass
3. `*domain.com/hangfire/*` → Cache Level: Bypass

---

## Workers

### Keep-alive (prevent Railway sleep)
- Script: `infra/cloudflare-worker-keepalive.js`
- Cron Trigger: `*/25 * * * *` (every 25 minutes)
- Env var: `API_HEALTH_URL = https://api.<domain>/health`

---

## Speed Optimizations

- **Auto Minify**: HTML, CSS, JS — all ON
- **Brotli**: ON
- **Early Hints**: ON
- **HTTP/2 to Origin**: ON (Railway supports it)
