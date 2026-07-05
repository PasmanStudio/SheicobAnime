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

    // Reel de NOTICIAS: máx. uno cada 24 h (motion card animada + música por IA
    // de la noticia más relevante del día). Apagar con Instagram__NewsReelEnabled=false.
    public bool NewsReelEnabled { get; set; } = true;

    // ── Reel "tráiler + titular" ────────────────────────────────────────
    // Si el artículo embebe un tráiler/PV de YouTube, el reel lo usa de fondo
    // (muteado, con nuestra música) en vez del slideshow de imágenes — el
    // formato de las cuentas grandes de noticias de anime. Sin tráiler o si
    // yt-dlp falla (IP bloqueada, video region-locked), cae al slideshow.
    public bool TrailerReelEnabled { get; set; } = true;

    // Binario de yt-dlp (el workflow lo instala con pipx; en dev local puede faltar).
    public string YtDlpPath { get; set; } = "yt-dlp";

    // player_client de yt-dlp: los clientes tv/ios evaden el check anti-bot que
    // YouTube tira contra IPs de datacenter (GitHub Actions). Si YouTube endurece
    // el bloqueo, ajustar acá sin redeploy (probar "tv", "ios", "mweb", etc.).
    public string YtDlpPlayerClients { get; set; } = "tv,ios,web_safari";

    // Duración del reel de tráiler en segundos (≤60 por si se reusa como story).
    public int TrailerClipSeconds { get; set; } = 18;

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
