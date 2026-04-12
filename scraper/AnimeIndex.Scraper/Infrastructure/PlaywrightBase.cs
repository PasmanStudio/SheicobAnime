using Microsoft.Playwright;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Abstract base for all Playwright-backed scrape strategies.
/// Manages browser/context/page lifecycle with anti-detection measures.
/// Call InitializeAsync() before scraping, DisposeAsync() when done (or wrap in await using).
/// </summary>
public abstract class PlaywrightBase : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _consecutiveFailures;

    protected IPage Page { get; private set; } = null!;

    /// <summary>Circuit breaker: consecutive failures before pausing.</summary>
    protected int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>How long to pause when circuit breaker trips (ms).</summary>
    protected int CircuitBreakerPauseMs { get; set; } = 600_000; // 10 minutes

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:125.0) Gecko/20100101 Firefox/125.0",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36 Edg/123.0.0.0"
    ];

    protected async Task InitializeAsync(bool headless = true)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-blink-features=AutomationControlled"
            ]
        });

        var ua = UserAgents[Random.Shared.Next(UserAgents.Length)];
        var widthJitter = Random.Shared.Next(-20, 21);
        var heightJitter = Random.Shared.Next(-20, 21);

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = ua,
            ViewportSize = new ViewportSize
            {
                Width = 1280 + widthJitter,
                Height = 720 + heightJitter
            },
            JavaScriptEnabled = true,
            Locale = "es-419",
            TimezoneId = "America/Buenos_Aires"
        });

        Page = await context.NewPageAsync();

        // Remove webdriver flag (stealth)
        await Page.AddInitScriptAsync("""
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        """);

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
    /// Returns false if navigation fails. Tracks consecutive failures for circuit breaker.
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

            if (response?.Ok ?? false)
            {
                _consecutiveFailures = 0;
                return true;
            }

            // Handle rate limiting with exponential backoff
            if (response?.Status is 429 or 503)
            {
                _consecutiveFailures++;
                var backoffMs = Math.Min(2000 * (1 << _consecutiveFailures), 60_000);
                await Task.Delay(backoffMs, ct);
            }
            else
            {
                _consecutiveFailures++;
            }

            return false;
        }
        catch (Exception)
        {
            _consecutiveFailures++;
            return false;
        }
    }

    /// <summary>
    /// Adds a jitter delay between requests. Base delay +/- 30%.
    /// </summary>
    protected static async Task JitterDelayAsync(int baseDelayMs, CancellationToken ct = default)
    {
        var jitter = (int)(baseDelayMs * 0.3);
        var actual = baseDelayMs + Random.Shared.Next(-jitter, jitter + 1);
        await Task.Delay(Math.Max(500, actual), ct);
    }

    /// <summary>
    /// Returns true if the circuit breaker has tripped (too many consecutive failures).
    /// Call this before each navigation; if true, pause and reset.
    /// </summary>
    protected async Task<bool> CheckCircuitBreakerAsync(CancellationToken ct = default)
    {
        if (_consecutiveFailures < CircuitBreakerThreshold)
            return false;

        await Task.Delay(CircuitBreakerPauseMs, ct);
        _consecutiveFailures = 0;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        GC.SuppressFinalize(this);
    }
}
