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

    // ─── Serilog ──────────────────────────────────────────
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console(new JsonFormatter()));

    // ─── Database ─────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? builder.Configuration["DATABASE_URL"]
        ?? throw new InvalidOperationException("No database connection string configured.");

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));

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
            job => job.RunAsync(CancellationToken.None),
            cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc, QueueName = "scraper" });
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
