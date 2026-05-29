import urllib.request, json, re

headers = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36',
    'Accept': 'text/html,*/*;q=0.8',
    'Accept-Language': 'es-419,es;q=0.9',
}

# ── STEP 1: Get series page, extract CSRF + AnimeId ──
series_url = 'https://jkanime.net/shingeki-no-kyojin/'
req = urllib.request.Request(series_url, headers=headers)
with urllib.request.urlopen(req, timeout=15) as r:
    html = r.read().decode('utf-8', 'replace')

csrf_m = re.search(r'name="csrf-token"\s+content="([^"]+)"', html)
csrf_token = csrf_m.group(1) if csrf_m else ''
print('CSRF:', csrf_token[:20])

anime_id_m = re.search(r'ajax/(?:personajes|votado|search_episode|episodes)/(\d+)', html)
if not anime_id_m:
    anime_id_m = re.search(r'data-id="(\d+)"', html)
anime_id = anime_id_m.group(1) if anime_id_m else None
print('AnimeID:', anime_id)

# ── STEP 2: Get episode list via AJAX ──
if anime_id and csrf_token:
    import urllib.parse
    ajax_url = 'https://jkanime.net/ajax/episodes/' + anime_id + '/1'
    data = urllib.parse.urlencode({'_token': csrf_token}).encode()
    req2 = urllib.request.Request(ajax_url, data=data, method='POST')
    req2.add_header('User-Agent', headers['User-Agent'])
    req2.add_header('X-Requested-With', 'XMLHttpRequest')
    req2.add_header('X-CSRF-TOKEN', csrf_token)
    req2.add_header('Referer', series_url)
    req2.add_header('Content-Type', 'application/x-www-form-urlencoded')
    try:
        with urllib.request.urlopen(req2, timeout=15) as r2:
            ep_data = json.loads(r2.read())
        print('Episodes AJAX works! Keys:', list(ep_data.keys()))
        if ep_data.get('data'):
            print('First ep:', ep_data['data'][0])
    except Exception as e:
        print('Episodes AJAX error:', str(e))

# ── STEP 3: Check if there's a servers AJAX endpoint ──
# Common pattern: /ajax/servers/{animeId}/{episodeNumber}
if anime_id and csrf_token:
    for endpoint in [
        'https://jkanime.net/ajax/servers/' + anime_id + '/1',
        'https://jkanime.net/ajax/episode/servers/' + anime_id + '/1',
        'https://jkanime.net/api/servers/' + anime_id + '/1',
    ]:
        data = urllib.parse.urlencode({'_token': csrf_token}).encode()
        req3 = urllib.request.Request(endpoint, data=data, method='POST')
        req3.add_header('User-Agent', headers['User-Agent'])
        req3.add_header('X-Requested-With', 'XMLHttpRequest')
        req3.add_header('X-CSRF-TOKEN', csrf_token)
        req3.add_header('Referer', series_url)
        req3.add_header('Content-Type', 'application/x-www-form-urlencoded')
        try:
            with urllib.request.urlopen(req3, timeout=10) as r3:
                body = r3.read().decode('utf-8', 'replace')
            print('ENDPOINT', endpoint, '-> 200, len:', len(body), 'body:', body[:200])
        except Exception as e:
            print('ENDPOINT', endpoint, '-> ERROR:', str(e)[:80])
