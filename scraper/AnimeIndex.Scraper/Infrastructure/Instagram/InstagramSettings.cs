namespace AnimeIndex.Scraper.Infrastructure.Instagram;

public class InstagramSettings
{
    // Meta Graph API long-lived access token (expires in 60 days — renew monthly)
    public string AccessToken { get; set; } = string.Empty;

    // Instagram Business Account numeric user ID (e.g. "17841400000000000")
    public string IgUserId { get; set; } = string.Empty;

    // imgbb.com API key — required to host generated images at a public HTTPS URL
    public string ImgBbApiKey { get; set; } = string.Empty;

    // Public-facing site URL used in captions and CTAs
    public string SiteUrl { get; set; } = "https://sheicobanime.com";

    // Instagram handle shown in captions (without @)
    public string Handle { get; set; } = "sheicobanime";

    // Max episodes per carousel (Instagram limit: 10). Episodes beyond this are skipped until tomorrow.
    public int MaxCarouselItems { get; set; } = 10;

    // Facebook App credentials — used ONLY for monthly token re-extension
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;

    // Meta Graph API version
    public string ApiVersion { get; set; } = "v22.0";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessToken) &&
        !string.IsNullOrWhiteSpace(IgUserId) &&
        !string.IsNullOrWhiteSpace(ImgBbApiKey);
}
