using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.Infrastructure.Resolvers;
using AnimeIndex.Scraper.Strategies;
using Microsoft.Extensions.Logging;

// MirrorScrapedData lives in the top-level AnimeIndex.Scraper namespace (ScrapeDataModels.cs)
using AnimeIndex.Scraper;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Orchestrates the upload-to-SeekStreaming pipeline for a single episode:
///   1. Pick the best embed URL from the existing external mirrors (VOE / Mp4Upload / OkRu first
///      because they return direct mp4 — no HLS token expiry risk).
///   2. Resolve that embed URL to a direct video URL using the existing resolver infrastructure.
///   3. Upload to SeekStreaming via remote URL (SeekStreaming fetches the video itself).
///   4. Upsert the resulting embed URL as a new mirror with priority=0 (always shown first).
///
/// This service is idempotent — if a 'seekstreaming' mirror already exists for the episode
/// UpsertPipelineService's ON CONFLICT logic will leave it untouched.
/// If all resolvers fail, returns false and the episode keeps its external mirrors unchanged.
/// </summary>
public sealed class SeekStreamingUploadService
{
    private readonly SeekStreamingClient _seekStreaming;
    private readonly ResolverRegistry _registry;
    private readonly UpsertPipelineService _upsert;
    private readonly ILogger<SeekStreamingUploadService> _logger;

    // Resolvers that return direct MP4 URLs (prioritised for tus upload).
    // VOE excluded: always returns HLS (.m3u8) from cloud IPs — not downloadable as a single file.
    // mp4upload first: highest quality, direct .mp4 link (port 183 accessible from GHA).
    private static readonly string[] Mp4FirstOrder =
        ["mp4upload", "okru", "yourupload", "sendvid", "streamwish", "vidhide", "filemoon"];

    public SeekStreamingUploadService(
        SeekStreamingClient seekStreaming,
        ResolverRegistry registry,
        UpsertPipelineService upsert,
        ILogger<SeekStreamingUploadService> logger)
    {
        _seekStreaming = seekStreaming;
        _registry = registry;
        _upsert = upsert;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to upload the episode to SeekStreaming using one of the provided embed URLs.
    /// Returns true if a SeekStreaming mirror was successfully upserted, false otherwise.
    /// Never throws — all failures are logged and swallowed.
    /// </summary>
    public async Task<bool> TryUploadEpisodeAsync(
        Guid episodeId,
        IReadOnlyList<string> embedUrls,
        CancellationToken ct = default)
    {
        if (embedUrls.Count == 0) return false;

        // Sort by upload priority: mp4-direct providers first, unsupported last.
        var sorted = embedUrls
            .Select(url => (Url: url, Provider: Source2Strategy.ExtractProviderName(url)))
            .Where(x => _registry.Supports(x.Provider))  // skip providers without a resolver
            .OrderBy(x =>
            {
                var idx = Array.IndexOf(Mp4FirstOrder, x.Provider);
                return idx < 0 ? 999 : idx;
            })
            .ToList();

        if (sorted.Count == 0)
        {
            _logger.LogDebug("Episode {EpisodeId}: no resolvable mirrors available for SeekStreaming upload", episodeId);
            return false;
        }

        foreach (var (embedUrl, provider) in sorted)
        {
            if (ct.IsCancellationRequested) return false;

            try
            {
                // Build a temporary Mirror record for the resolver.
                var tempMirror = new Mirror
                {
                    Id = Guid.NewGuid(),
                    EpisodeId = episodeId,
                    ProviderName = provider,
                    EmbedUrl = embedUrl,
                    QualityLabel = 720,
                    Priority = 50,
                };

                _logger.LogDebug(
                    "Episode {EpisodeId}: resolving via {Provider} ({EmbedUrl})",
                    episodeId, provider, embedUrl);

                var resolver = _registry.GetFor(tempMirror);
                if (resolver is null) continue;

                var resolved = await resolver.ResolveAsync(tempMirror, ct);

                _logger.LogDebug(
                    "Episode {EpisodeId}: resolved {Provider} → {Format} {ResolvedUrl}",
                    episodeId, provider, resolved.Format, resolved.Url[..Math.Min(80, resolved.Url.Length)]);

                // Reject HLS manifests — cannot be downloaded as a single file without ffmpeg.
                if (resolved.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                    resolved.Format == AnimeIndex.Api.Infrastructure.Resolvers.SourceFormat.Hls)
                {
                    _logger.LogDebug(
                        "Episode {EpisodeId}: skipping {Provider} — resolved to HLS manifest (not a single-file MP4)",
                        episodeId, provider);
                    continue;
                }

                // Reject known test/placeholder video domains — they are served by some
                // providers (e.g. VOE) as geo-block fallbacks and must never be uploaded.
                if (IsTestVideoUrl(resolved.Url))
                {
                    _logger.LogDebug(
                        "Episode {EpisodeId}: skipping {Provider} — resolved to test-video placeholder URL ({Url})",
                        episodeId, provider, resolved.Url[..Math.Min(80, resolved.Url.Length)]);
                    continue;
                }

                var seekEmbedUrl = await _seekStreaming.UploadFromUrlAsync(resolved.Url, ct: ct);
                if (seekEmbedUrl is null) continue;

                await _upsert.UpsertMirrorAsync(new MirrorScrapedData(
                    EpisodeId: episodeId,
                    ProviderName: "seekstreaming",
                    EmbedUrl: seekEmbedUrl,
                    QualityLabel: 720,
                    Priority: 0), ct);

                _logger.LogInformation(
                    "Episode {EpisodeId}: SeekStreaming mirror upserted (embedUrl={EmbedUrl}, source={Provider})",
                    episodeId, seekEmbedUrl, provider);

                return true;
            }
            catch (ResolverException rex)
            {
                _logger.LogDebug(
                    "Episode {EpisodeId}: resolver {Provider} failed ({Reason}): {Message}",
                    episodeId, provider, rex.Reason, rex.Message);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Episode {EpisodeId}: unexpected error resolving {Provider}",
                    episodeId, provider);
            }
        }

        _logger.LogWarning(
            "Episode {EpisodeId}: all {Count} resolver attempts failed — keeping external mirrors only",
            episodeId, sorted.Count);
        return false;
    }

    // Domains known to serve BigBuckBunny or other placeholder content as geo-block fallbacks.
    private static readonly string[] TestVideoDomains =
    [
        "test-videos.co.uk",
        "commondatastorage.googleapis.com",
        "download.blender.org",
        "upload.wikimedia.org",
    ];

    private static bool IsTestVideoUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return Array.Exists(TestVideoDomains, d =>
            uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }
}
