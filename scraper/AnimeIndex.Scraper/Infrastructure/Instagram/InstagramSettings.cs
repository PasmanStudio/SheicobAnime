namespace AnimeIndex.Scraper.Infrastructure.Instagram;

public class InstagramSettings
{
    // Meta Graph API long-lived access token (expires in 60 days — renew monthly)
    public string AccessToken { get; set; } = string.Empty;

    // Instagram Business Account numeric user ID (e.g. "17841400000000000")
    public string IgUserId { get; set; } = string.Empty;

    // imgbb.com API key — LEGACY image host (free tier rate-limits hard under the
    // hourly news volume). Used only as fallback when R2 is not configured.
    public string ImgBbApiKey { get; set; } = string.Empty;

    // ── Cloudinary image hosting (preferred over imgbb) ────────────────────
    // Meta requires a public HTTPS URL for every image. We upload to Cloudinary,
    // which authenticates by API key (NOT by IP) so it's immune to the shared
    // GitHub-Actions-IP throttle that makes imgbb fail from CI. When all three are
    // set, Cloudinary is used; otherwise it falls back to imgbb.
    public string CloudinaryCloudName { get; set; } = string.Empty;
    public string CloudinaryApiKey { get; set; } = string.Empty;
    public string CloudinaryApiSecret { get; set; } = string.Empty;
    // Optional folder to keep the uploaded promo images tidy in the media library.
    public string CloudinaryFolder { get; set; } = "ig";

    // Public-facing site URL used in captions and CTAs.
    // Override via Instagram__SiteUrl secret (e.g. https://sheicobanime.sheicob.workers.dev)
    public string SiteUrl { get; set; } = "https://sheicobanime.sheicob.workers.dev";

    // Instagram handle shown in captions (without @)
    public string Handle { get; set; } = "sheicobanime";

    // Max episodes per carousel (Instagram limit: 10). Episodes beyond this are skipped until tomorrow.
    public int MaxCarouselItems { get; set; } = 10;

    // ── Reels (motion cards) ────────────────────────────────────────────
    // Reel de EPISODIOS (uno por corrida del scraper). Default OFF — el usuario
    // quiere el reel diario para las NOTICIAS; prender con Instagram__ReelsEnabled=true.
    public bool ReelsEnabled { get; set; } = false;

    // Reel de NOTICIAS (slideshow/tráiler + música por IA de la noticia más
    // relevante del pool). La cadencia la fija el cron vía AnimeNews__RunFormat
    // (5 corridas "reel" por día); sin ese env var, máx. uno cada 24 h.
    // Apagar del todo con Instagram__NewsReelEnabled=false.
    public bool NewsReelEnabled { get; set; } = true;

    // ── Reel "tráiler + titular" ────────────────────────────────────────
    // Si el artículo embebe un tráiler/PV de YouTube, el reel lo usa de fondo
    // (muteado, con nuestra música) en vez del slideshow de imágenes — el
    // formato de las cuentas grandes de noticias de anime. Sin tráiler o si
    // yt-dlp falla (IP bloqueada, video region-locked), cae al slideshow.
    public bool TrailerReelEnabled { get; set; } = true;

    // Búsqueda ACTIVA del tráiler en YouTube cuando el artículo no embebe
    // ninguno (el caso típico): Gemini decide si la noticia amerita video
    // (anuncio de tráiler/temporada/película) y arma la query; yt-dlp busca
    // (ytsearch) y se descarga el mejor candidato. Best-effort → slideshow.
    public bool TrailerSearchEnabled { get; set; } = true;

    // Último recurso de la cadena de tráiler (decisión del usuario, jul-2026):
    // sin versión en español ni subtítulos es manuales para quemar, el tráiler
    // OFICIAL va igual en su idioma original — el titular y las slides en
    // español encima le dan el contexto (mismo trato que openings/cortos).
    // false = regla estricta anterior (PR #154): sin español → slideshow.
    public bool TrailerOriginalLanguageFallback { get; set; } = true;

    // Respaldos cuando YouTube falla (18-jul-2026: la descarga está bloqueada
    // desde CI — 34 combos cliente×cookies×WARP; X y bilibili NO bloquean a
    // los runners). Gobierna la escalera completa: tweet embebido del artículo
    // → post de X buscado por la IA con grounding (URL validada contra su
    // metadata real) → búsqueda en bilibili como última red. Corre siempre que
    // la noticia amerite video, aunque la búsqueda de YouTube no dé candidato.
    public bool TweetVideoFallback { get; set; } = true;

    // Binario de yt-dlp (el workflow lo instala con pipx; en dev local puede faltar).
    public string YtDlpPath { get; set; } = "yt-dlp";

    // player_client de yt-dlp. VACÍO = los clientes por defecto de yt-dlp, que
    // es lo correcto desde ago-2026: el 2-ago-2026 YouTube empezó a exigirle a
    // android_vr un GVS PO token (yt-dlp#17348) y sin él descarta TODOS los
    // formatos https salvo el 18 — que además responde 403 en la descarga.
    // Reproducido el 22-ago-2026 con la misma nightly de CI desde IP
    // residencial limpia: android_vr → "ERROR: unable to download video data:
    // HTTP Error 403"; los clientes por defecto + un runtime JS (ver
    // YtDlpJsRuntimes) bajan 720p sin PO token. Esto tiró ~60% de los reels sin
    // video entre el 17 y el 21-ago. Se deja configurable para poder volver a
    // fijar un cliente sin redeploy si YouTube cambia otra vez.
    public string YtDlpPlayerClients { get; set; } = string.Empty;

    // player_client para el REINTENTO tras un bot-check. Repetir el mismo
    // comando rinde poco: el bloqueo va por (cliente, video, IP) y el
    // 24-ago-2026 los dos intentos idénticos de Tokyo Revengers comieron el
    // mismo "Sign in to confirm you're not a bot". web_embedded ataca el player
    // por otra vía y resuelve 720p igual que los clientes por defecto
    // (verificado 24-ago-2026 con la nightly de CI). Vacío = reintento igual.
    public string YtDlpRetryPlayerClients { get; set; } = "web_embedded";

    // Cuántos candidatos de la búsqueda se intentan bajar antes de rendirse.
    // Cada intento fallido cuesta ~3 s, así que 4 acota el peor caso a ~25 s
    // sin dejar de cubrir el caso real (6 tráilers válidos, el 1ro bloqueado).
    public int MaxVideoCandidates { get; set; } = 4;

    // Archivo de cookies de YouTube (formato Netscape) para yt-dlp. Es el
    // remedio documentado para el bot-check "Sign in to confirm you're not a
    // bot", que quedó como ÚNICA causa de reels sin video una vez muerto el 403
    // del PO token: pega aunque WARP esté arriba, cuando la IP de salida del
    // túnel viene flagueada (22-ago-2026). Se habían sacado en jul-2026 porque
    // ROMPÍAN a android_vr — ese cliente ya no se usa, así que el motivo no
    // aplica más. SOLO se mandan a YouTube (ver IsYouTube): las cookies de
    // sesión no tienen por qué viajar a bilibili ni a X. Vacío = sin cookies.
    public string YtDlpCookiesPath { get; set; } = string.Empty;

    // Runtimes JS que yt-dlp puede usar para resolver el "n challenge" de
    // YouTube (EJS). Desde ago-2026 extraer sin runtime está DEPRECADO: sin uno,
    // los clientes por defecto devuelven "Only images are available". yt-dlp
    // solo habilita "deno" por default y el runner de GitHub NO lo trae, pero sí
    // trae node — de ahí el default. La opción se repite por runtime en la línea
    // de comandos (no acepta lista separada por comas).
    public string YtDlpJsRuntimes { get; set; } = "node";

    // Proxy de salida para yt-dlp (p. ej. "socks5h://127.0.0.1:1080"). YouTube
    // bloquea por IP a los runners de GitHub — confirmado jul-2026: las 21
    // combinaciones cliente×cookies fallaron con "Sign in to confirm you're not
    // a bot". El workflow levanta Cloudflare WARP (wgcf + wireproxy) y pasa el
    // SOCKS5 local acá; la descarga sale por IP de WARP (no-datacenter).
    // Vacío = conexión directa (dev local, donde la IP residencial no está
    // bloqueada). SOLO se aplica a URLs de YouTube: bilibili responde HTTP 412
    // (Precondition Failed) a las IPs de WARP — 5 de 13 corridas de la última
    // red murieron así entre el 18 y el 21-ago-2026.
    public string YtDlpProxy { get; set; } = string.Empty;

    // Máximo de segundos de TRÁILER en el reel (la gente quiere VER el tráiler
    // — cortarlo a los 18s era matar el formato). Si el tráiler dura menos, se
    // usa lo que haya; el total del reel (tráiler + slides informativas + CTA)
    // se capa solo a ~60s.
    public int TrailerClipSeconds { get; set; } = 45;

    // ── Suno (música generada FRESCA por reel, vía sunoapi.org — tercero) ──
    // Con la key configurada, cada reel de noticias genera un track instrumental
    // nuevo (mood + estilo por IA). Sin key o si falla → biblioteca Cloudinary/CC.
    // Los tracks generados se siembran en Cloudinary ({folder}/music) como fallback.
    public string SunoApiKey { get; set; } = string.Empty;
    public string SunoApiUrl { get; set; } = "https://api.sunoapi.org";
    public string SunoModel { get; set; } = "V4_5";

    public bool SunoConfigured => !string.IsNullOrWhiteSpace(SunoApiKey);

    // Ruta del binario de ffmpeg (default: "ffmpeg" en PATH — preinstalado en
    // ubuntu-latest). Sin ffmpeg el publisher degrada a imagen sin fallar.
    public string FfmpegPath { get; set; } = "ffmpeg";

    // Duración del Reel en segundos (specs de la API: 3 s a 15 min; las stories
    // de video aceptan máximo 60 s — mantener ≤60 para reusar el mismo MP4).
    public int ReelDurationSeconds { get; set; } = 12;

    // Música del Reel: Gemini clasifica la serie en un mood y se mezcla un track
    // CC BY (Kevin MacLeod) con atribución en el caption. Sin Gemini usa una
    // heurística por géneros. Apagar con Instagram__ReelMusicEnabled=false.
    public bool ReelMusicEnabled { get; set; } = true;

    // Facebook App credentials — used ONLY for monthly token re-extension
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;

    // Meta Graph API version
    public string ApiVersion { get; set; } = "v25.0";

    // Cloudinary is preferred; imgbb is the legacy fallback.
    public bool CloudinaryConfigured =>
        !string.IsNullOrWhiteSpace(CloudinaryCloudName) &&
        !string.IsNullOrWhiteSpace(CloudinaryApiKey) &&
        !string.IsNullOrWhiteSpace(CloudinaryApiSecret);

    public bool ImgBbConfigured => !string.IsNullOrWhiteSpace(ImgBbApiKey);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessToken) &&
        !string.IsNullOrWhiteSpace(IgUserId) &&
        (CloudinaryConfigured || ImgBbConfigured);
}
