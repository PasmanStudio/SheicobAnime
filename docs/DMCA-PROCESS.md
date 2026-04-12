# DMCA / Takedown Compliance Process

SheicobAnime is an anime streaming **index** — it never hosts video files.
Despite this, we honour DMCA and takedown requests promptly.

## How it works

1. **Receive** a takedown notice (email, GitHub issue, or legal form).
2. **Add** the slug to the `blocked_slugs` table via the Admin API:
   ```bash
   curl -X POST https://API_URL/admin/blocked-slugs \
     -H "Content-Type: application/json" \
     -H "X-Admin-Key: $ADMIN_API_KEY" \
     -d '{"slug": "series-slug", "reason": "DMCA takedown request from Studio X"}'
   ```
3. The scraper checks `blocked_slugs` **before every scrape** — the slug is
   never re-indexed.
4. Existing data for the blocked series is **soft-deleted** (mirrors marked
   inactive, series hidden from public endpoints).
5. Reply to the requester confirming removal within 24 hours.

## Verifying a block

```bash
# List all blocked slugs
curl https://API_URL/admin/blocked-slugs \
  -H "X-Admin-Key: $ADMIN_API_KEY"

# Confirm series no longer appears in public API
curl https://API_URL/series/SLUG   # should 404
```

## Removing a block

If a takedown is rescinded or was issued in error:

```bash
curl -X DELETE https://API_URL/admin/blocked-slugs/SLUG \
  -H "X-Admin-Key: $ADMIN_API_KEY"
```

The series will be re-indexed on the next scraper run.

## Contact

Takedown requests: open a GitHub issue with `[DMCA]` in the title, or email
the address listed in the site footer.
