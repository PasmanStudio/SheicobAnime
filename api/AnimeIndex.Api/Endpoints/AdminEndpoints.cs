using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.DTOs.Admin;
using AnimeIndex.Api.Infrastructure.Auth;
using AnimeIndex.Api.Infrastructure.Cache;
using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin")
            .AddEndpointFilter<AdminKeyEndpointFilter>()
            .RequireRateLimiting("admin");

        group.MapPost("/scrape-jobs", CreateScrapeJob);
        group.MapGet("/scrape-jobs", ListScrapeJobs);
        group.MapDelete("/series/{id:guid}", DeleteSeries);
        group.MapPost("/blocked-slugs", CreateBlockedSlug);
        group.MapGet("/blocked-slugs", ListBlockedSlugs);
        group.MapDelete("/blocked-slugs/{slug}", DeleteBlockedSlug);
        group.MapPost("/backfill", CreateBackfill);
        group.MapGet("/backfill/{id:guid}/progress", GetBackfillProgress);
        group.MapDelete("/mirrors/purge-invalid", PurgeInvalidMirrors);
        group.MapPost("/scrape-jobs/{id:guid}/cancel", CancelScrapeJob);
    }

    private static async Task<IResult> CreateScrapeJob(
        AppDbContext db,
        IValidator<CreateScrapeJobRequest> validator,
        CreateScrapeJobRequest request,
        CancellationToken ct = default)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Results.Json(
                new ErrorResponse("Validation failed", "VALIDATION_ERROR",
                    validation.Errors.ToDictionary(e => e.PropertyName, e => e.ErrorMessage)),
                statusCode: 422);

        var job = new ScrapeJob
        {
            JobType = $"scrape:{request.Source}",
            Status = "pending",
            ScheduledAt = DateTime.UtcNow,
            // SeriesId set later by the scraper when it identifies the series
            ErrorMessage = request.SourceUrl // Store URL temporarily in ErrorMessage for the worker to pick up
        };

        db.ScrapeJobs.Add(job);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/admin/scrape-jobs/{job.Id}", new { jobId = job.Id, status = "queued" });
    }

    private static async Task<IResult> ListScrapeJobs(
        AppDbContext db,
        int page = 1,
        int pageSize = 20,
        string? status = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = db.ScrapeJobs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(j => j.Status == status);

        query = query.OrderByDescending(j => j.ScheduledAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Results.Ok(new PaginatedResponse<object>(
            items.Select(j => new
            {
                j.Id,
                j.SeriesId,
                j.JobType,
                j.Status,
                j.AttemptCount,
                j.ErrorMessage,
                j.ScheduledAt,
                j.CompletedAt
            }).ToArray<object>(),
            total, page, pageSize));
    }

    private static async Task<IResult> DeleteSeries(
        AppDbContext db,
        ICacheService cache,
        Guid id,
        CancellationToken ct = default)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
            return Results.Json(
                new ErrorResponse("Series not found", "NOT_FOUND"),
                statusCode: 404);

        db.Series.Remove(series);
        await db.SaveChangesAsync(ct);

        // Invalidate cache
        await cache.RemoveAsync($"series:{series.Slug}", ct);

        return Results.NoContent();
    }

    private static async Task<IResult> CreateBlockedSlug(
        AppDbContext db,
        IValidator<CreateBlockedSlugRequest> validator,
        CreateBlockedSlugRequest request,
        CancellationToken ct = default)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Results.Json(
                new ErrorResponse("Validation failed", "VALIDATION_ERROR",
                    validation.Errors.ToDictionary(e => e.PropertyName, e => e.ErrorMessage)),
                statusCode: 422);

        var exists = await db.BlockedSlugs.AnyAsync(b => b.Slug == request.Slug, ct);
        if (exists)
            return Results.Json(
                new ErrorResponse("Slug is already blocked", "DUPLICATE"),
                statusCode: 409);

        db.BlockedSlugs.Add(new BlockedSlug
        {
            Slug = request.Slug,
            Reason = request.Reason,
            BlockedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        return Results.Created($"/admin/blocked-slugs/{request.Slug}", new { slug = request.Slug });
    }

    private static async Task<IResult> ListBlockedSlugs(
        AppDbContext db,
        CancellationToken ct = default)
    {
        var slugs = await db.BlockedSlugs
            .AsNoTracking()
            .OrderBy(b => b.Slug)
            .ToListAsync(ct);

        return Results.Ok(slugs.Select(b => new { b.Slug, b.Reason, b.BlockedAt }));
    }

    private static async Task<IResult> DeleteBlockedSlug(
        AppDbContext db,
        string slug,
        CancellationToken ct = default)
    {
        var blocked = await db.BlockedSlugs.FirstOrDefaultAsync(b => b.Slug == slug, ct);
        if (blocked is null)
            return Results.Json(
                new ErrorResponse("Blocked slug not found", "NOT_FOUND"),
                statusCode: 404);

        db.BlockedSlugs.Remove(blocked);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> CreateBackfill(
        AppDbContext db,
        IValidator<CreateBackfillRequest> validator,
        CreateBackfillRequest request,
        CancellationToken ct = default)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Results.Json(
                new ErrorResponse("Validation failed", "VALIDATION_ERROR",
                    validation.Errors.ToDictionary(e => e.PropertyName, e => e.ErrorMessage)),
                statusCode: 422);

        // Prevent duplicate running backfills for the same source
        var existing = await db.ScrapeJobs
            .AnyAsync(j => j.JobType == $"backfill:{request.Source}"
                        && (j.Status == "pending" || j.Status == "running"), ct);
        if (existing)
            return Results.Json(
                new ErrorResponse("A backfill job for this source is already running", "DUPLICATE_BACKFILL"),
                statusCode: 409);

        var job = new ScrapeJob
        {
            JobType = $"backfill:{request.Source}",
            Status = "pending",
            ScheduledAt = DateTime.UtcNow,
            ErrorMessage = $"{{\"maxPages\":{request.MaxPages},\"progress\":\"queued\"}}"
        };

        db.ScrapeJobs.Add(job);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/admin/backfill/{job.Id}/progress", new { jobId = job.Id, status = "queued", maxPages = request.MaxPages });
    }

    private static async Task<IResult> GetBackfillProgress(
        AppDbContext db,
        Guid id,
        CancellationToken ct = default)
    {
        var job = await db.ScrapeJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is null)
            return Results.Json(
                new ErrorResponse("Backfill job not found", "NOT_FOUND"),
                statusCode: 404);

        return Results.Ok(new
        {
            job.Id,
            job.JobType,
            job.Status,
            job.AttemptCount,
            Progress = job.ErrorMessage,
            job.ScheduledAt,
            job.CompletedAt
        });
    }

    /// <summary>
    /// Purges mirrors with invalid embed URLs (JKAnime wrapper URLs, static assets, etc.)
    /// that should never have been stored. Returns count of deleted records.
    /// </summary>
    private static async Task<IResult> PurgeInvalidMirrors(
        AppDbContext db,
        CancellationToken ct = default)
    {
        // Find mirrors that are JKAnime wrapper URLs or static assets — not real embeds
        var invalidMirrors = await db.Mirrors
            .Where(m =>
                m.EmbedUrl.Contains("jkanime.net") ||
                m.EmbedUrl.Contains("jkdesa.com") ||
                m.EmbedUrl.Contains("jkplayer") ||
                m.EmbedUrl.EndsWith(".js") ||
                m.EmbedUrl.EndsWith(".css"))
            .ToListAsync(ct);

        if (invalidMirrors.Count == 0)
            return Results.Ok(new { purged = 0, message = "No invalid mirrors found." });

        db.Mirrors.RemoveRange(invalidMirrors);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { purged = invalidMirrors.Count, message = $"Purged {invalidMirrors.Count} invalid mirror(s)." });
    }

    private static async Task<IResult> CancelScrapeJob(
        AppDbContext db,
        Guid id,
        CancellationToken ct = default)
    {
        var job = await db.ScrapeJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return Results.Json(new ErrorResponse("Job not found", "NOT_FOUND"), statusCode: 404);

        if (job.Status is not ("pending" or "running"))
            return Results.Json(new ErrorResponse($"Job is already {job.Status}", "INVALID_STATE"), statusCode: 409);

        job.Status = "failed";
        job.ErrorMessage = "cancelled by admin";
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { job.Id, job.Status, message = "Job cancelled." });
    }
}
