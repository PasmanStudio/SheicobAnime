using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using AnimeIndex.Scraper.Jobs;
using AnimeIndex.Scraper.Strategies;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Json;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new JsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // ─── Production env var validation (fail fast) ───────
    if (builder.Environment.IsProduction())
    {
        if (string.IsNullOrEmpty(builder.Configuration["DATABASE_URL"])
            && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("DefaultConnection")))
            throw new InvalidOperationException("Missing required environment variable: DATABASE_URL");
    }

    // ─── Serilog ──────────────────────────────────────────
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "scraper")
        .WriteTo.Console(new JsonFormatter()));

    // ─── Database ─────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(connectionString))
        connectionString = builder.Configuration["DATABASE_URL"];
    if (string.IsNullOrEmpty(connectionString))
        throw new InvalidOperationException("No database connection string configured.");

    // Convert postgresql:// URI to ADO.NET format (Railway/Supabase use URI format)
    connectionString = NormalizePostgresConnectionString(connectionString);

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null);
            npgsqlOptions.CommandTimeout(60); // scraper upserts can be slower
        }));

    // ─── Hangfire (shared PostgreSQL with API) ────────────
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(opts =>
        {
            opts.UseNpgsqlConnection(connectionString);
        },
        new Hangfire.PostgreSql.PostgreSqlStorageOptions
        {
            // GHA runs up to 6 hours. Prevent a second Hangfire worker from
            // re-queuing the same job after the default 30-min window.
            InvisibilityTimeout = TimeSpan.FromHours(7),
        }));

    builder.Services.AddHangfireServer((BackgroundJobServerOptions options) =>
    {
        options.WorkerCount = 1; // single worker — scrape jobs are long (hours)
        options.Queues = ["scraper", "default"];
    });

    // ─── HTTP clients ─────────────────────────────────────
    builder.Services.AddHttpClient("probe", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
    });
    builder.Services.AddHttpClient("resend", c =>
    {
        c.BaseAddress = new Uri("https://api.resend.com");
        c.Timeout = TimeSpan.FromSeconds(15);
    });
    builder.Services.AddHttpClient("resolver", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    });
    builder.Services.AddHttpClient("seekstreaming", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(30);
    });

    // ─── Scraper services ─────────────────────────────────
    builder.Services.AddScoped<MirrorProbeService>();
    builder.Services.AddScoped<UpsertPipelineService>();
    builder.Services.AddScoped<DeadLetterAlerter>();
    builder.Services.AddScoped<JkAnimeHttpClient>();

    // ─── Hoster resolvers (reuse same impls as API) ───────
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.VoeResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.Mp4UploadResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.OkruResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.StreamwishResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.VidhideResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.ResolverRegistry>();

    // ─── SeekStreaming upload pipeline ────────────────────
    builder.Services.AddSingleton<SeekStreamingClient>();
    builder.Services.AddScoped<SeekStreamingUploadService>();

    // ─── Scrape strategies (all IScrapeStrategy impls) ────
    // Source1 (AnimeFlv) removed — consistently blocked by Cloudflare, 0 data indexed.
    // Note: Playwright removed — all scraping now uses pure HTTP (10-50x faster).
    builder.Services.AddScoped<IScrapeStrategy, Source2Strategy>();

    // ─── Hangfire job classes ─────────────────────────────
    builder.Services.AddScoped<ScrapeOrchestratorJob>();
    builder.Services.AddScoped<ScrapeSchedulerJob>();
    builder.Services.AddScoped<BackfillJob>();
    builder.Services.AddScoped<WatchProgressCleanupJob>();
    builder.Services.AddScoped<MirrorHealthCheckJob>();

    var app = builder.Build();

    // ─── Register recurring jobs ──────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var recurring = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

        // Single scheduler — runs daily at 02:00 UTC (23:00 Argentina)
        var scraperCron = builder.Configuration["Hangfire:SchedulerCron"] ?? "0 2 * * *";
        recurring.AddOrUpdate<ScrapeSchedulerJob>(
            "scrape-scheduler",
            "scraper",
            job => job.RunAsync(CancellationToken.None),
            scraperCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Remove legacy source1 scheduler if it exists
        recurring.RemoveIfExists("scrape-scheduler-source2");

        // Daily cleanup of anonymous watch_progress rows (180-day retention)
        recurring.AddOrUpdate<WatchProgressCleanupJob>(
            "watch-progress-cleanup",
            "scraper",
            job => job.RunAsync(CancellationToken.None),
            "0 3 * * *", // 03:00 UTC daily
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Daily mirror health check — probes 500 oldest-checked mirrors
        recurring.AddOrUpdate<MirrorHealthCheckJob>(
            "mirror-health-check",
            "scraper",
            job => job.RunAsync(CancellationToken.None),
            "0 4 * * *", // 04:00 UTC daily
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Auto-create initial scrape jobs if none exist (first deployment bootstrap)
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasJobs = await db.ScrapeJobs.AnyAsync(j => j.Status == "pending" || j.Status == "running");
        if (!hasJobs)
        {
            Log.Information("No pending/running scrape jobs found — creating initial job for source2");
            db.ScrapeJobs.Add(new AnimeIndex.Api.Data.Entities.ScrapeJob
            {
                JobType = "scrape:source2",
                Status = "pending",
                ScheduledAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Trigger scheduler immediately on startup to process any pending jobs
        // (instead of waiting for the next cron tick)
        var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
        jobClient.Enqueue<ScrapeSchedulerJob>(j => j.RunAsync(CancellationToken.None));
        Log.Information("Enqueued immediate ScrapeSchedulerJob run on startup");
    }

    await app.RunAsync();
}
catch (HostAbortedException)
{
    // Expected when EF Core design-time tools build the host then abort it.
}
catch (Exception ex)
{
    Log.Fatal(ex, "Scraper worker terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// Converts a postgresql:// URI to ADO.NET connection string format.
/// Hangfire.PostgreSql does not support URI format natively.
/// </summary>
static string NormalizePostgresConnectionString(string input)
{
    if (!input.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
        && !input.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        return input;

    var uri = new Uri(input);
    var userInfo = uri.UserInfo.Split(':', 2);
    var user = Uri.UnescapeDataString(userInfo[0]);
    var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
    var host = uri.Host;
    var port = uri.Port > 0 ? uri.Port : 5432;
    var database = uri.AbsolutePath.TrimStart('/');

    return $"Host={host};Port={port};Database={database};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=3";
}
