using System.Security.Cryptography;
using System.Text;

namespace AnimeIndex.Api.Infrastructure.Proxy;

/// <summary>
/// Signs and verifies short-lived HMAC tokens for the /proxy/stream endpoint so
/// third parties can't abuse Sheicob as an open relay. Tokens embed the upstream
/// URL + Referer + expiry and are signed with a server-side secret.
/// </summary>
public sealed class ProxyUrlSigner
{
    private readonly byte[] _key;
    public const int DefaultTtlSeconds = 60 * 30; // 30 min matches resolver cache window

    public ProxyUrlSigner(IConfiguration config)
    {
        var secret = config["ADMIN_API_KEY"]
            ?? config["AdminApiKey"]
            ?? Environment.GetEnvironmentVariable("ADMIN_API_KEY")
            ?? "dev-only-proxy-signing-key";
        _key = Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>
    /// Builds a proxy URL. If <paramref name="absoluteBase"/> is provided (e.g. "https://api.example.com"),
    /// returns an absolute URL; otherwise returns a relative path.
    /// </summary>
    public string BuildProxyPath(string upstreamUrl, string? referer, string? absoluteBase = null, int? ttlSeconds = null)
    {
        var exp = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds ?? DefaultTtlSeconds).ToUnixTimeSeconds();
        var u = UrlB64Encode(upstreamUrl);
        var r = UrlB64Encode(referer ?? string.Empty);
        var sig = Sign($"{u}.{r}.{exp}");
        var path = $"/proxy/stream?u={u}&r={r}&exp={exp}&sig={sig}";
        return string.IsNullOrEmpty(absoluteBase) ? path : $"{absoluteBase.TrimEnd('/')}{path}";
    }

    public bool TryVerify(string u, string r, long exp, string sig, out string url, out string referer)
    {
        url = string.Empty;
        referer = string.Empty;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;
        var expected = Sign($"{u}.{r}.{exp}");
        if (!FixedTimeEquals(expected, sig)) return false;
        try
        {
            url = UrlB64Decode(u);
            referer = UrlB64Decode(r);
        }
        catch
        {
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        return true;
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return UrlB64EncodeBytes(hash);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aa = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aa, bb);
    }

    private static string UrlB64Encode(string value) =>
        UrlB64EncodeBytes(Encoding.UTF8.GetBytes(value));

    private static string UrlB64EncodeBytes(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string UrlB64Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
