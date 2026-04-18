using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// The ONLY interface used to extract a fresh playable URL from a third-party hoster embed.
/// Resolvers are request-time: never cache the result in DB, only in-memory with TTL &lt; ExpiresAt.
/// Never call concrete resolvers directly — always go through ResolverRegistry.
/// </summary>
public interface IHosterResolver
{
    /// <summary>Lowercase hoster name this resolver supports (e.g., "mp4upload", "streamwish").</summary>
    string Hoster { get; }

    /// <summary>True if this resolver does NOT require a headless browser (faster, less memory).</summary>
    bool IsHttpOnly { get; }

    /// <summary>
    /// Resolve the hoster embed URL into a fresh playable source.
    /// Throws ResolverException on failure. Never returns null.
    /// </summary>
    Task<ResolvedSource> ResolveAsync(Mirror mirror, CancellationToken ct = default);
}

public sealed class ResolverException : Exception
{
    public string Hoster { get; }
    public ResolverFailureReason Reason { get; }

    public ResolverException(string hoster, ResolverFailureReason reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Hoster = hoster;
        Reason = reason;
    }
}

public enum ResolverFailureReason
{
    NotSupported,
    EmbedUnavailable,
    PatternChanged,
    NetworkError,
    Timeout,
    Blocked
}
