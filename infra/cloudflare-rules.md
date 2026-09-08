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

### Keep-alive (evita que Render duerma el API)
- Worker: `infra/keepalive/` (`sheicobanime-keepalive`)
- Deploy: automático desde `.github/workflows/keepalive-worker.yml`
- Cron Triggers: `*/5 13-23 * * *` y `*/5 0-5 * * *` — 13:00-05:59 UTC
  (10:00-02:59 ART). NO es 24/7: el free tier de Render son ~750 h de instancia
  al mes para todo el workspace y un mes de 31 días son 744 h, así que
  mantenerlo despierto siempre no entra. Fuera de esa ventana el sitio se sirve
  del cache de KV.
- Config: `API_BASE_URL` y `WARM_PATHS` en `infra/keepalive/wrangler.jsonc`
- El cron equivalente en GitHub Actions (`keepalive-cron.yml`) quedó como
  respaldo: GHA corría el `*/10` cada ~167 min en la práctica y nunca evitó un
  spin-down. Ver el encabezado de ese archivo para los números.

---

## Speed Optimizations

- **Auto Minify**: HTML, CSS, JS — all ON
- **Brotli**: ON
- **Early Hints**: ON
- **HTTP/2 to Origin**: ON (Railway supports it)
