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
        .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString)));

    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = 2;
        options.Queues = ["scraper", "default"];
    });

    // ─── HTTP clients ─────────────────────────────────────
    builder.Services.AddHttpClient("probe", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SheicobAnime-Probe/1.0");
    });
    builder.Services.AddHttpClient("resend", c =>
    {
        c.BaseAddress = new Uri("https://api.resend.com");
        c.Timeout = TimeSpan.FromSeconds(15);
    });

    // ─── Scraper services ─────────────────────────────────
    builder.Services.AddScoped<MirrorProbeService>();
    builder.Services.AddScoped<UpsertPipelineService>();
    builder.Services.AddScoped<DeadLetterAlerter>();

    // ─── Scrape strategies (all IScrapeStrategy impls) ────
    builder.Services.AddScoped<IScrapeStrategy, Source1Strategy>();
    builder.Services.AddScoped<IScrapeStrategy, Source2Strategy>();

    // ─── Hangfire job classes ─────────────────────────────
    builder.Services.AddScoped<ScrapeOrchestratorJob>();
    builder.Services.AddScoped<ScrapeSchedulerJob>();

    var app = builder.Build();

    // ─── Register recurring jobs ──────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var recurring = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
        var cron = builder.Configuration["Hangfire:SchedulerCron"] ?? Cron.Hourly();

        recurring.AddOrUpdate<ScrapeSchedulerJob>(
            "scrape-scheduler",
            "scraper",
            job => job.RunAsync(CancellationToken.None),
            cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Source2 (JKAnime) runs on a separate schedule — every 2 hours by default
        var source2Cron = builder.Configuration["Hangfire:Source2Cron"] ?? "0 */2 * * *";
        recurring.AddOrUpdate<ScrapeSchedulerJob>(
            "scrape-scheduler-source2",
            "scraper",
            job => job.RunAsync(CancellationToken.None),
            source2Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    await app.RunAsync();
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

    return $"Host={host};Port={port};Database={database};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true";
}
