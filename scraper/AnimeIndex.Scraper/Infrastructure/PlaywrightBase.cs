using Microsoft.Playwright;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Abstract base for all Playwright-backed scrape strategies.
/// Manages browser/context/page lifecycle. Call InitializeAsync() before scraping,
/// DisposeAsync() when done (or wrap in await using).
/// </summary>
public abstract class PlaywrightBase : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    protected IPage Page { get; private set; } = null!;

    protected async Task InitializeAsync(bool headless = true)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
        });

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (compatible; SheicobBot/1.0; +https://sheicobanime.com/bot)",
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
            JavaScriptEnabled = true
        });

        Page = await context.NewPageAsync();

        // Block images/fonts/media to speed up scraping
        await Page.RouteAsync("**/*", async route =>
        {
            var resourceType = route.Request.ResourceType;
            if (resourceType is "image" or "media" or "font" or "stylesheet")
                await route.AbortAsync();
            else
                await route.ContinueAsync();
        });
    }

    /// <summary>
    /// Navigate to a URL and wait for the DOM to settle.
    /// Returns false if navigation fails.
    /// </summary>
    protected async Task<bool> GoToAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var response = await Page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000
            });
            return response?.Ok ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        GC.SuppressFinalize(this);
    }
}
