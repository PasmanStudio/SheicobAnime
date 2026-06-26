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

    // ── Cloudflare R2 image hosting (preferred over imgbb) ─────────────────
    // Meta requires a public HTTPS URL for every image. We upload the generated
    // images to a public R2 bucket (S3-compatible) — no third-party rate limits.
    // When all five are set, R2 is used; otherwise it falls back to imgbb.
    public string R2AccountId { get; set; } = string.Empty;         // Cloudflare account id (the r2.cloudflarestorage.com subdomain)
    public string R2AccessKeyId { get; set; } = string.Empty;       // R2 API token: Access Key ID
    public string R2SecretAccessKey { get; set; } = string.Empty;   // R2 API token: Secret Access Key
    public string R2Bucket { get; set; } = string.Empty;            // bucket name (e.g. "sheicobanime-ig")
    // Public base URL of the bucket — the r2.dev managed domain or a custom domain,
    // NO trailing slash (e.g. "https://pub-xxxx.r2.dev" or "https://img.sheicobanime.com")
    public string R2PublicBaseUrl { get; set; } = string.Empty;

    // Public-facing site URL used in captions and CTAs.
    // Override via Instagram__SiteUrl secret (e.g. https://sheicobanime.sheicob.workers.dev)
    public string SiteUrl { get; set; } = "https://sheicobanime.sheicob.workers.dev";

    // Instagram handle shown in captions (without @)
    public string Handle { get; set; } = "sheicobanime";

    // Max episodes per carousel (Instagram limit: 10). Episodes beyond this are skipped until tomorrow.
    public int MaxCarouselItems { get; set; } = 10;

    // Facebook App credentials — used ONLY for monthly token re-extension
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;

    // Meta Graph API version
    public string ApiVersion { get; set; } = "v25.0";

    // R2 is preferred; imgbb is the legacy fallback.
    public bool R2Configured =>
        !string.IsNullOrWhiteSpace(R2AccountId) &&
        !string.IsNullOrWhiteSpace(R2AccessKeyId) &&
        !string.IsNullOrWhiteSpace(R2SecretAccessKey) &&
        !string.IsNullOrWhiteSpace(R2Bucket) &&
        !string.IsNullOrWhiteSpace(R2PublicBaseUrl);

    public bool ImgBbConfigured => !string.IsNullOrWhiteSpace(ImgBbApiKey);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessToken) &&
        !string.IsNullOrWhiteSpace(IgUserId) &&
        (R2Configured || ImgBbConfigured);
}
