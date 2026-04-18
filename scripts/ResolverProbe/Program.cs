using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.Infrastructure.Resolvers;
using Microsoft.Extensions.DependencyInjection;

// Real-world resolver probe.
// Tests the actual resolvers from AnimeIndex.Api against real production mirror URLs.

var services = new ServiceCollection();
services.AddHttpClient("resolver", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "es-ES,es;q=0.9,en;q=0.8");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = true,
    AutomaticDecompression = DecompressionMethods.All,
    MaxAutomaticRedirections = 5,
});
var sp = services.BuildServiceProvider();
var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

var mirrors = new[]
{
    new { Hoster = "streamwish", Url = "https://sfastwish.com/e/c2fl307yw4rg" },
    new { Hoster = "vidhide",    Url = "https://vidhidevip.com/embed/o3zpuy8qksv6" },
    new { Hoster = "mp4upload",  Url = "https://www.mp4upload.com/embed-vmr4f84e9pz0.html" },
};

IHosterResolver MakeResolver(string hoster) => hoster switch
{
    "streamwish" => new StreamwishResolver(httpFactory),
    "vidhide"    => new VidhideResolver(httpFactory),
    "mp4upload"  => new Mp4UploadResolver(httpFactory),
    _ => throw new NotSupportedException(hoster),
};

foreach (var m in mirrors)
{
    Console.WriteLine($"\n=== {m.Hoster.ToUpper()} — {m.Url} ===");
    var resolver = MakeResolver(m.Hoster);
    var mirror = new Mirror
    {
        Id = Guid.NewGuid(),
        EpisodeId = Guid.NewGuid(),
        ProviderName = m.Hoster,
        EmbedUrl = m.Url,
        QualityLabel = 720,
        Priority = 1,
    };

    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await resolver.ResolveAsync(mirror);
        sw.Stop();
        Console.WriteLine($"  [+] RESOLVED in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"      Format   : {result.Format}");
        Console.WriteLine($"      Url      : {result.Url}");
        Console.WriteLine($"      Proxy    : {result.ProxyRequired}");
        Console.WriteLine($"      Expires  : {result.ExpiresAt}");
        if (result.Headers is not null)
            foreach (var h in result.Headers)
                Console.WriteLine($"      Header   : {h.Key}: {h.Value}");

        // Try to fetch the URL with headers to confirm it's actually playable
        try
        {
            var probe = httpFactory.CreateClient("resolver");
            using var preq = new HttpRequestMessage(HttpMethod.Get, result.Url);
            if (result.Headers is not null)
                foreach (var h in result.Headers)
                    preq.Headers.TryAddWithoutValidation(h.Key, h.Value);
            preq.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            using var pres = await probe.SendAsync(preq);
            var body = await pres.Content.ReadAsStringAsync();
            Console.WriteLine($"      ProbeHTTP: {(int)pres.StatusCode} ({body.Length} bytes)");
            if (body.StartsWith("#EXTM3U"))
                Console.WriteLine($"      [+] Valid HLS manifest (first line: #EXTM3U)");
            else if (body.Length < 500)
                Console.WriteLine($"      [!] Body preview: {body.Replace("\n", "\\n")[..Math.Min(200, body.Length)]}");
        }
        catch (Exception px)
        {
            Console.WriteLine($"      [!] Probe of resolved URL failed: {px.Message}");
        }
    }
    catch (ResolverException rx)
    {
        Console.WriteLine($"  [X] FAILED: {rx.Reason} — {rx.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [X] ERROR: {ex.GetType().Name} — {ex.Message}");
    }
}

Console.WriteLine("\n=== DONE ===");

// ---- Deep dump: fetch + unpack manually to inspect what's really in the player ----
Console.WriteLine("\n=== DEEP DUMP: unpacked content ===");
var dumpUrls = new[]
{
    ("streamwish", "https://sfastwish.com/e/c2fl307yw4rg", "https://sfastwish.com/"),
    ("vidhide",    "https://vidhidevip.com/embed/o3zpuy8qksv6", "https://vidhidevip.com/"),
    ("mp4upload",  "https://www.mp4upload.com/embed-vmr4f84e9pz0.html", "https://www.mp4upload.com/"),
};
foreach (var (name, url, referer) in dumpUrls)
{
    Console.WriteLine($"\n--- {name}: {url} ---");
    try
    {
        var client = httpFactory.CreateClient("resolver");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", referer);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        using var res = await client.SendAsync(req);
        var html = await res.Content.ReadAsStringAsync();
        Console.WriteLine($"  status={(int)res.StatusCode} size={html.Length}");
        if (!res.IsSuccessStatusCode) continue;

        var unpacked = LocalUnpacker.Unpack(html);
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dumps");
        Directory.CreateDirectory(dir);
        var rawPath = Path.Combine(dir, $"{name}.raw.html");
        var upPath  = Path.Combine(dir, $"{name}.unpacked.txt");
        File.WriteAllText(rawPath, html);
        File.WriteAllText(upPath, unpacked);
        Console.WriteLine($"  wrote {rawPath}");
        Console.WriteLine($"  wrote {upPath}");

        // Scan unpacked for anything that looks like a playable source
        var urlRx = new Regex(@"https?://[^\s""'\\<>]+\.(?:m3u8|mp4|mpd)[^\s""'\\<>]*", RegexOptions.IgnoreCase);
        var hits = urlRx.Matches(unpacked).Select(m => m.Value).Distinct().ToList();
        Console.WriteLine($"  media URLs in unpacked: {hits.Count}");
        foreach (var h in hits.Take(8)) Console.WriteLine($"    {h}");

        // Also look for common shapes
        var shapes = new[]
        {
            @"file\s*:\s*[""'][^""']+[""']",
            @"sources?\s*:\s*\[\s*\{[^}]+\}",
            @"src\s*:\s*[""'][^""']+[""']",
            @"hls\s*:\s*[""'][^""']+[""']",
            @"player\.src\s*\([^)]+\)",
        };
        foreach (var sh in shapes)
        {
            var m = Regex.Match(unpacked, sh, RegexOptions.IgnoreCase);
            if (m.Success)
                Console.WriteLine($"  shape /{sh}/: {m.Value[..Math.Min(200, m.Value.Length)]}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR: {ex.Message}");
    }
}
