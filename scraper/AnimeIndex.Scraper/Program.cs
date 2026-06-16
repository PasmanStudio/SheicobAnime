using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using AnimeIndex.Scraper.Infrastructure.Discord;
using AnimeIndex.Scraper.Infrastructure.Instagram;
using AnimeIndex.Scraper.Infrastructure.Telegram;
using AnimeIndex.Scraper.Jobs;
using AnimeIndex.Scraper.Strategies;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Json;

// ── Anime news pipeline (lightweight, no Hangfire) ───────────────────────────
// Usage: dotnet run --project scraper/AnimeIndex.Scraper -- --news
// Designed for a dedicated GHA workflow that runs every hour.
// Fetches RSS, scrapes full articles, posts 1 item to Instagram, exits.
if (args.Contains("--news"))
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console(new JsonFormatter())
        .CreateBootstrapLogger();

    try
    {
        var newsBuilder = Host.CreateApplicationBuilder(args);

        newsBuilder.Services.AddSerilog((_, lc) => lc
            .ReadFrom.Configuration(newsBuilder.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", "news")
            .WriteTo.Console(new JsonFormatter()));

        // DB
        // appsettings.json has DefaultConnection="" (empty string, not null) — must use IsNullOrEmpty.
        // The ?? operator doesn't help here: "" ?? fallback returns "", skipping the fallback.
        var newsConnStr = newsBuilder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrEmpty(newsConnStr))
            newsConnStr = newsBuilder.Configuration["DATABASE_URL"];
        if (string.IsNullOrEmpty(newsConnStr))
            throw new InvalidOperationException("Missing DATABASE_URL / ConnectionStrings:DefaultConnection");
        newsConnStr = NormalizePostgresConnectionString(newsConnStr);
        newsBuilder.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(newsConnStr, o => o.EnableRetryOnFailure(3)));

        // Settings
        var newsIgSettings = new AnimeIndex.Scraper.Infrastructure.Instagram.InstagramSettings();
        newsBuilder.Configuration.GetSection("Instagram").Bind(newsIgSettings);
        newsBuilder.Services.AddSingleton(newsIgSettings);

        var newsSettings = new AnimeIndex.Scraper.Infrastructure.AnimeNewsSettings();
        newsBuilder.Configuration.GetSection("AnimeNews").Bind(newsSettings);
        newsBuilder.Services.AddSingleton(newsSettings);

        // HTTP clients
        newsBuilder.Services.AddHttpClient("news-rss", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (compatible; SheicobAnime-NewsBot/1.0)");
        });
        newsBuilder.Services.AddHttpClient("instagram-graph", c => c.Timeout = TimeSpan.FromSeconds(30));
        newsBuilder.Services.AddHttpClient("probe", c => c.Timeout = TimeSpan.FromSeconds(15));

        // Services
        newsBuilder.Services.AddScoped<AnimeIndex.Scraper.Infrastructure.AnimeNewsFeedService>();
        newsBuilder.Services.AddScoped<AnimeIndex.Scraper.Infrastructure.Instagram.AnimeNewsImageService>();
        newsBuilder.Services.AddScoped<AnimeIndex.Scraper.Infrastructure.Instagram.MetaGraphApiClient>();
        newsBuilder.Services.AddScoped<AnimeIndex.Scraper.Infrastructure.Instagram.AnimeNewsPublisherService>();
        newsBuilder.Services.AddScoped<AnimeIndex.Scraper.Jobs.AnimeNewsJob>();

        var newsApp = newsBuilder.Build();
        // Run as one-shot: migrate → run job → exit
        await using var newsScope = newsApp.Services.CreateAsyncScope();
        // Apply pending migrations (creates anime_news_items if it doesn't exist yet)
        var newsDb = newsScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await newsDb.Database.MigrateAsync();
        var newsJob = newsScope.ServiceProvider.GetRequiredService<AnimeIndex.Scraper.Jobs.AnimeNewsJob>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8)); // safety timeout
        await newsJob.RunAsync(cts.Token);
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "News pipeline terminated unexpectedly");
        throw;
    }
    finally
    {
        await Log.CloseAndFlushAsync();
    }
    return;
}

// ── Quick local image-generation test (no DB, no Hangfire, no Instagram creds) ──
// Usage: dotnet run --project scraper/AnimeIndex.Scraper -- --images
if (args.Contains("--images"))
{
    await using var sp = new ServiceCollection()
        .AddLogging(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }))
        .AddHttpClient()
        .BuildServiceProvider();

    await AnimeIndex.Scraper.Infrastructure.Instagram.TestImageGenerator.RunAsync(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>());
    return;
}

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

    // ─── Instagram settings ────────────────────────────────
    var igSettings = new AnimeIndex.Scraper.Infrastructure.Instagram.InstagramSettings();
    builder.Configuration.GetSection("Instagram").Bind(igSettings);
    builder.Services.AddSingleton(igSettings);

    // ─── Anime news settings ───────────────────────────────
    var animeNewsSettings = new AnimeIndex.Scraper.Infrastructure.AnimeNewsSettings();
    builder.Configuration.GetSection("AnimeNews").Bind(animeNewsSettings);
    builder.Services.AddSingleton(animeNewsSettings);

    // ─── Discord settings ──────────────────────────────────
    var discordSettings = new DiscordSettings();
    builder.Configuration.GetSection("Discord").Bind(discordSettings);
    builder.Services.AddSingleton(discordSettings);

    // ─── Telegram settings ─────────────────────────────────
    var telegramSettings = new TelegramSettings();
    builder.Configuration.GetSection("Telegram").Bind(telegramSettings);
    builder.Services.AddSingleton(telegramSettings);

    var webPushSettings = new AnimeIndex.Scraper.Infrastructure.Notifications.WebPushSettings();
    builder.Configuration.GetSection("WebPush").Bind(webPushSettings);
    builder.Services.AddSingleton(webPushSettings);

    // ─── HTTP clients ─────────────────────────────────────
    builder.Services.AddHttpClient("discord", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(15);
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SheicobAnime-Scraper/1.0");
    });
    builder.Services.AddHttpClient("telegram", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SheicobAnime-Scraper/1.0");
    });
    builder.Services.AddHttpClient("instagram-graph", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(30);
    });
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
    // SeekStreaming API calls (api-token header added in SeekStreamingClient constructor)
    builder.Services.AddHttpClient("seekstreaming", c =>
    {
        c.BaseAddress = new Uri("https://seekstreaming.com");
        c.Timeout = TimeSpan.FromSeconds(30);
    });
    // Large-file download client for tus upload pipeline (streams source MP4)
    builder.Services.AddHttpClient("seek-download", c =>
    {
        c.Timeout = TimeSpan.FromMinutes(90);
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    });
    // tus PATCH client — no base URL, generous timeout for large uploads
    builder.Services.AddHttpClient("seek-tus", c =>
    {
        c.Timeout = TimeSpan.FromMinutes(90);
    });
    // Multi-host upload client (DoodStream / Voe) — local upload: el POST sube el
    // archivo entero, así que necesita timeout largo (igual que las subidas a Seek).
    builder.Services.AddHttpClient("multihost", c =>
    {
        c.Timeout = TimeSpan.FromMinutes(90);
    });
    // RSS feed fetcher — reads anime news XML feeds
    builder.Services.AddHttpClient("news-rss", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (compatible; SheicobAnime-NewsBot/1.0; +https://sheicobanime.sheicob.workers.dev)");
    });

    // ─── Discord publishing services ───────────────────────
    builder.Services.AddScoped<DiscordWebhookClient>();
    builder.Services.AddScoped<DiscordPublisherService>();

    // ─── Telegram publishing services ──────────────────────
    builder.Services.AddScoped<TelegramBotClient>();
    builder.Services.AddScoped<TelegramPublisherService>();

    builder.Services.AddScoped<AnimeIndex.Scraper.Infrastructure.Notifications.WebPushPublisherService>();

    // Revalidación on-demand del frontend (purga el cache KV al terminar el scrape)
    builder.Services.AddScoped<AnimeIndex.Scraper.Infrastructure.SiteRevalidationService>();

    // ─── Instagram publishing services ────────────────────
    builder.Services.AddScoped<MetaGraphApiClient>();
    builder.Services.AddScoped<InstagramImageService>();
    builder.Services.AddScoped<CaptionGeneratorService>();
    builder.Services.AddScoped<InstagramPublisherService>();

    // ─── Anime news RSS + Instagram news pipeline ─────────
    builder.Services.AddScoped<AnimeIndex.Scraper.Infrastructure.AnimeNewsFeedService>();
    builder.Services.AddScoped<AnimeIndex.Scraper.Infrastructure.Instagram.AnimeNewsImageService>();
    builder.Services.AddScoped<AnimeIndex.Scraper.Infrastructure.Instagram.AnimeNewsPublisherService>();

    // ─── Scraper services ─────────────────────────────────
    builder.Services.AddScoped<MirrorProbeService>();
    builder.Services.AddScoped<UpsertPipelineService>();
    builder.Services.AddScoped<DeadLetterAlerter>();
    builder.Services.AddScoped<JkAnimeHttpClient>();
    builder.Services.AddScoped<KatanimeHttpClient>();
    builder.Services.AddSingleton<SeekStreamingClient>();
    builder.Services.AddSingleton<TusVideoUploader>();
    builder.Services.AddScoped<MultiHostUploadService>();
    builder.Services.AddScoped<SeekStreamingUploadService>();

    // ─── Hoster resolvers (reuse same impls as API) ───────
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.VoeResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.Mp4UploadResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.OkruResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.StreamwishResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.VidhideResolver>();
    // Extra resolvers that katanime commonly exposes and that aren't IP-bound — high-value for the fallback.
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.MediafireResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.MixdropResolver>();
    // Streamtape: MP4 directo y cloud-friendly — alto valor como fuente de upload.
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.IHosterResolver, AnimeIndex.Api.Infrastructure.Resolvers.StreamtapeResolver>();
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Resolvers.ResolverRegistry>();

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
    builder.Services.AddScoped<AnimeNewsJob>();

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

        // Anime news RSS → Instagram: each hour from 11:00–23:00 UTC = 8 AM–8 PM Argentina (UTC-3).
        // No posts during madrugada. MaxPerRun=1 → at most 1 post/run.
        // Override via Hangfire:NewsJobCron to adjust the window.
        // Examples:
        //   "0 11-23 * * *"   → 8 AM–8 PM Argentina (default)
        //   "0 12-22 * * *"   → 9 AM–7 PM Argentina (narrower)
        //   "0 11-23,0,1 * * *" → extend to midnight + 1 AM Argentina
        var newsCron = builder.Configuration["Hangfire:NewsJobCron"] ?? "0 11-23 * * *";
        recurring.AddOrUpdate<AnimeNewsJob>(
            "anime-news",
            "scraper",
            job => job.RunAsync(CancellationToken.None),
            newsCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Apply any pending EF Core migrations (safety net if deploy workflow skipped them)
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        // Auto-create initial scrape jobs if none exist (first deployment bootstrap)
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
