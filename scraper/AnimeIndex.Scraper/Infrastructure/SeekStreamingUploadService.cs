using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.Infrastructure.Resolvers;
using AnimeIndex.Scraper.Strategies;
using Microsoft.Extensions.Logging;

// MirrorScrapedData lives in the top-level AnimeIndex.Scraper namespace (ScrapeDataModels.cs)
using AnimeIndex.Scraper;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Orchestrates the upload-to-SeekStreaming pipeline for a single episode.
///
/// Two-phase design:
///   Phase A — ResolveDirectUrlAsync: probe embed URLs to find the first downloadable direct MP4.
///             This is fast (HTTP only) and runs sequentially in Pass3 before any upload starts.
///   Phase B — UploadResolvedAsync: download the MP4 bytes and stream them to SeekStreaming via tus.
///             This is slow (hundreds of MB) and runs fully in parallel via Task.WhenAll.
///
/// Splitting phases lets us fail-fast on unresolvable episodes before wasting parallel slots,
/// and ensures all downloads start at the same moment for maximum throughput.
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

    // ── Phase A: resolve embed URL → direct downloadable MP4 URL ────────────

    /// <summary>
    /// Resolves ALL embed URLs that can produce a downloadable direct MP4 (not HLS).
    /// Returns them ordered by priority (mp4upload first, then okru, etc.).
    /// An empty list means no provider worked — episode should be skipped.
    ///
    /// Unlike the old single-result version, this lets Phase B fall back to the
    /// next provider if the primary CDN download fails (e.g. cloud-IP block on mp4upload port 183).
    /// Never throws — all failures are logged and swallowed.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedUploadTarget>> ResolveAllDirectUrlsAsync(
        Guid episodeId,
        IReadOnlyList<string> embedUrls,
        CancellationToken ct = default)
    {
        if (embedUrls.Count == 0) return [];

        var sorted = embedUrls
            .Select(url => (Url: url, Provider: Source2Strategy.ExtractProviderName(url)))
            .Where(x => _registry.Supports(x.Provider))
            .OrderBy(x =>
            {
                var idx = Array.IndexOf(Mp4FirstOrder, x.Provider);
                return idx < 0 ? 999 : idx;
            })
            .ToList();

        if (sorted.Count == 0)
        {
            _logger.LogDebug("Episode {Id}: no resolvable mirrors", episodeId);
            return [];
        }

        var results = new List<ResolvedUploadTarget>(sorted.Count);
        foreach (var (embedUrl, provider) in sorted)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var tempMirror = new Mirror
                {
                    Id = Guid.NewGuid(),
                    EpisodeId = episodeId,
                    ProviderName = provider,
                    EmbedUrl = embedUrl,
                    QualityLabel = 720,
                    Priority = 50,
                };

                var resolver = _registry.GetFor(tempMirror);
                if (resolver is null) continue;

                var resolved = await resolver.ResolveAsync(tempMirror, ct);

                if (resolved.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                    resolved.Format == AnimeIndex.Api.Infrastructure.Resolvers.SourceFormat.Hls)
                {
                    _logger.LogDebug("Episode {Id}: {Provider} resolved to HLS — skipping", episodeId, provider);
                    continue;
                }

                if (IsTestVideoUrl(resolved.Url))
                {
                    _logger.LogDebug("Episode {Id}: {Provider} resolved to placeholder URL — skipping", episodeId, provider);
                    continue;
                }

                var referer = Uri.TryCreate(embedUrl, UriKind.Absolute, out var eu)
                    ? $"{eu.Scheme}://{eu.Host}/"
                    : null;

                _logger.LogDebug(
                    "Episode {Id}: resolved via {Provider} → {Url}",
                    episodeId, provider, resolved.Url[..Math.Min(80, resolved.Url.Length)]);

                results.Add(new ResolvedUploadTarget(episodeId, resolved.Url, referer, provider));
            }
            catch (ResolverException rex)
            {
                _logger.LogDebug("Episode {Id}: {Provider} failed ({Reason}): {Message}",
                    episodeId, provider, rex.Reason, rex.Message);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Episode {Id}: unexpected error resolving {Provider}", episodeId, provider);
            }
        }

        if (results.Count == 0)
            _logger.LogWarning("Episode {Id}: all {Count} resolver attempts failed", episodeId, sorted.Count);
        else
            _logger.LogInformation(
                "Episode {Id}: {Count} resolvable provider(s): [{Providers}]",
                episodeId, results.Count, string.Join(", ", results.Select(r => r.Provider)));

        return results;
    }

    /// <summary>
    /// Compatibility shim — returns only the highest-priority resolvable URL.
    /// Used by Source2Strategy (single-episode, non-bulk path).
    /// For bulk backfill use <see cref="ResolveAllDirectUrlsAsync"/> + per-candidate fallback.
    /// </summary>
    public async Task<ResolvedUploadTarget?> ResolveDirectUrlAsync(
        Guid episodeId,
        IReadOnlyList<string> embedUrls,
        CancellationToken ct = default)
    {
        var all = await ResolveAllDirectUrlsAsync(episodeId, embedUrls, ct);
        return all.Count > 0 ? all[0] : null;
    }

    // ── Phase B: download + tus upload + upsert ──────────────────────────────

    /// <summary>
    /// Downloads the pre-resolved direct URL and uploads it to SeekStreaming via tus,
    /// then upserts the resulting embed URL as a priority=0 mirror.
    /// Returns true on success. Never throws.
    /// </summary>
    public async Task<bool> UploadResolvedAsync(
        ResolvedUploadTarget target,
        CancellationToken ct = default)
    {
        try
        {
            var seekEmbedUrl = await _seekStreaming.UploadFromUrlAsync(
                target.DirectUrl,
                referer: target.Referer,
                ct: ct);

            if (seekEmbedUrl is null)
            {
                _logger.LogWarning("\u274c Episode {Id}: SeekStreaming upload failed — tus returned no embed URL", target.EpisodeId);
                return false;
            }

            await _upsert.UpsertMirrorAsync(new MirrorScrapedData(
                EpisodeId: target.EpisodeId,
                ProviderName: "seekstreaming",
                EmbedUrl: seekEmbedUrl,
                QualityLabel: 720,
                Priority: 0), ct);

            _logger.LogInformation(
                "\u2705 Episode {Id}: SeekStreaming video created OK (embed={Embed}, source={Provider})",
                target.EpisodeId, seekEmbedUrl, target.Provider);

            return true;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "\u274c Episode {Id}: SeekStreaming upload failed (exception)", target.EpisodeId);
            return false;
        }
    }

    // ── Domains known to serve BigBuckBunny or other placeholder content ─────

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

/// <summary>
/// A direct downloadable video URL found during the resolve phase of Pass3.
/// </summary>
public sealed record ResolvedUploadTarget(
    Guid EpisodeId,
    string DirectUrl,
    string? Referer,
    string Provider);
