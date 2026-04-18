using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AnimeIndex.Api.Infrastructure.Proxy;

/// <summary>
/// Builds and verifies opaque tokens for the /proxy/stream endpoint.
///
/// The payload <c>{u, r, e}</c> (upstream URL, referer, unix-seconds expiry) is
/// serialized as JSON and encrypted with AES-256-GCM using a key derived from
/// ADMIN_API_KEY. Because GCM is AEAD, the 16-byte auth tag authenticates the
/// ciphertext, so no separate HMAC is needed. The upstream URL
/// (e.g. a4.mp4upload.com/...) is never exposed to the client — DevTools only
/// sees an opaque blob, which makes it materially harder to scrape Sheicob's
/// resolver work.
///
/// Token layout (before URL-safe Base64): [12 B nonce][ciphertext][16 B tag].
/// </summary>
public sealed class ProxyUrlSigner
{
    public const int DefaultTtlSeconds = 60 * 30; // 30 min matches resolver cache window
    private const int NonceSize = 12;   // AES-GCM standard nonce size
    private const int TagSize = 16;     // AES-GCM standard tag size

    private readonly byte[] _key; // 32 bytes (AES-256)

    public ProxyUrlSigner(IConfiguration config)
    {
        var secret = config["ADMIN_API_KEY"]
            ?? config["AdminApiKey"]
            ?? Environment.GetEnvironmentVariable("ADMIN_API_KEY")
            ?? "dev-only-proxy-signing-key";
        // Derive a 256-bit key from whatever secret the operator provided so
        // the on-disk secret is never used directly as a cryptographic key.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>
    /// Builds a proxy URL. If <paramref name="absoluteBase"/> is provided
    /// (e.g. "https://api.example.com"), returns an absolute URL; otherwise a
    /// relative path. The returned URL contains a single opaque <c>t=</c> token.
    /// </summary>
    public string BuildProxyPath(string upstreamUrl, string? referer, string? absoluteBase = null, int? ttlSeconds = null)
    {
        var exp = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds ?? DefaultTtlSeconds).ToUnixTimeSeconds();
        var token = Encrypt(new TokenPayload(upstreamUrl, referer ?? string.Empty, exp));
        var path = $"/proxy/stream?t={token}";
        return string.IsNullOrEmpty(absoluteBase) ? path : $"{absoluteBase.TrimEnd('/')}{path}";
    }

    /// <summary>Verifies an opaque token and extracts the upstream URL + referer.</summary>
    public bool TryVerify(string token, out string url, out string referer)
    {
        url = string.Empty;
        referer = string.Empty;

        TokenPayload? payload;
        try
        {
            payload = Decrypt(token);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }

        if (payload is null) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > payload.E) return false;

        if (!Uri.TryCreate(payload.U, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;

        url = payload.U;
        referer = payload.R;
        return true;
    }

    private string Encrypt(TokenPayload payload)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(_key, TagSize))
        {
            gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var combined = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + ciphertext.Length, TagSize);
        return UrlB64EncodeBytes(combined);
    }

    private TokenPayload? Decrypt(string token)
    {
        var combined = UrlB64DecodeBytes(token);
        if (combined.Length < NonceSize + TagSize) return null;

        var ctLen = combined.Length - NonceSize - TagSize;
        var nonce = new byte[NonceSize];
        var ciphertext = new byte[ctLen];
        var tag = new byte[TagSize];
        Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(combined, NonceSize, ciphertext, 0, ctLen);
        Buffer.BlockCopy(combined, NonceSize + ctLen, tag, 0, TagSize);

        var plaintext = new byte[ctLen];
        using (var gcm = new AesGcm(_key, TagSize))
        {
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return JsonSerializer.Deserialize<TokenPayload>(plaintext);
    }

    private static string UrlB64EncodeBytes(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] UrlB64DecodeBytes(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    // Short property names keep the ciphertext (and therefore the URL) compact.
    private sealed record TokenPayload(string U, string R, long E);
}
