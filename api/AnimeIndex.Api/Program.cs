using System.Threading.RateLimiting;
using AnimeIndex.Api;
using AnimeIndex.Api.Data;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.DTOs.Admin;
using AnimeIndex.Api.Endpoints;
using AnimeIndex.Api.Infrastructure.Auth;
using AnimeIndex.Api.Infrastructure.Cache;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Api.Validators;
using FluentValidation;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Serilog;
using StackExchange.Redis;

// Check env var directly — WebApplicationFactory sets this before entry point runs
var isTesting = string.Equals(
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
    "Testing", StringComparison.OrdinalIgnoreCase);

if (!isTesting)
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
        .CreateBootstrapLogger();
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Production env var validation (fail fast) ───────
    if (builder.Environment.IsProduction())
    {
        var missing = new List<string>();
        if (string.IsNullOrEmpty(builder.Configuration["DATABASE_URL"])
            && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("DefaultConnection")))
            missing.Add("DATABASE_URL");
        if (string.IsNullOrEmpty(builder.Configuration["REDIS_URL"])
            && string.IsNullOrEmpty(builder.Configuration.GetConnectionString("Redis")))
            missing.Add("REDIS_URL");
        if (string.IsNullOrEmpty(builder.Configuration["ADMIN_API_KEY"]))
            missing.Add("ADMIN_API_KEY");
        if (string.IsNullOrEmpty(builder.Configuration["CORS_ORIGINS"]))
            missing.Add("CORS_ORIGINS");

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing required environment variables for production: {string.Join(", ", missing)}");
    }

    // ─── Serilog (skip in tests to avoid "logger already frozen") ──
    if (!isTesting)
    {
        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter()));
    }

    // ─── Sentry ──────────────────────────────────────────
    var sentryEnabled = false;
    if (!isTesting)
    {
        var sentryDsn = builder.Configuration["SENTRY_DSN"];
        if (!string.IsNullOrEmpty(sentryDsn))
        {
            builder.WebHost.UseSentry(o =>
            {
                o.Dsn = sentryDsn;
                o.TracesSampleRate = 0.1; // 10% of transactions — free tier friendly
                o.SendDefaultPii = false;
                o.Environment = builder.Environment.EnvironmentName;
            });
            sentryEnabled = true;
        }
    }

    // ─── Database ────────────────────────────────────────
    if (!isTesting)
    {
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
                npgsqlOptions.CommandTimeout(30);
            }));

        // ─── Hangfire ────────────────────────────────────
        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString)));
        builder.Services.AddHangfireServer(options =>
        {
            options.WorkerCount = 2; // MVP: keep low
        });
    }

    // ─── Redis / Cache ───────────────────────────────────
    if (!isTesting)
    {
        var redisConnection = builder.Configuration.GetConnectionString("Redis")
            ?? builder.Configuration["REDIS_URL"]
            ?? "localhost:6379";

        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnection));
        builder.Services.AddSingleton<ICacheService, RedisCacheService>();
    }

    // ─── Mapster ─────────────────────────────────────────
    MappingConfig.RegisterMappings();

    // ─── FluentValidation ────────────────────────────────
    builder.Services.AddScoped<IValidator<CreateScrapeJobRequest>, CreateScrapeJobValidator>();
    builder.Services.AddScoped<IValidator<CreateBlockedSlugRequest>, CreateBlockedSlugValidator>();

    // ─── Rate Limiting ───────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetSlidingWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));

        // Stricter limit for admin endpoints
        options.AddPolicy("admin", context =>
            RateLimitPartition.GetSlidingWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 2,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));

        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = 429;
            await context.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse("Rate limit exceeded.", "RATE_LIMITED"), ct);
        };
    });

    // ─── CORS ────────────────────────────────────────────
    var corsOrigins = builder.Configuration["CORS_ORIGINS"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? ["http://localhost:3000"];

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(corsOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
    });

    // ─── Auth filters (transient for DI) ─────────────────
    builder.Services.AddTransient<AdminKeyEndpointFilter>();

    // ─── HTTP clients ─────────────────────────────────────
    builder.Services.AddHttpClient("probe", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SheicobAnime-Probe/1.0");
    });
    builder.Services.AddScoped<MirrorProbeService>();

    var app = builder.Build();

    // ─── Middleware pipeline ─────────────────────────────

    // Correlation ID: propagate or generate X-Correlation-Id for request tracing
    app.Use(async (context, next) =>
    {
        const string header = "X-Correlation-Id";
        if (!context.Request.Headers.TryGetValue(header, out var correlationId)
            || string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }
        context.Items["CorrelationId"] = correlationId.ToString();
        context.Response.Headers[header] = correlationId.ToString();

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId.ToString()))
        {
            await next();
        }
    });

    if (!isTesting) app.UseSerilogRequestLogging(opts =>
    {
        opts.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            diagnosticContext.Set("CorrelationId", httpContext.Items["CorrelationId"]?.ToString() ?? "");
        };
    });
    if (sentryEnabled) app.UseSentryTracing();

    // Security headers
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        if (!context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        await next();
    });

    app.UseRateLimiter();
    app.UseCors();

    // ─── Hangfire Dashboard ──────────────────────────────
    if (!app.Environment.EnvironmentName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
    {
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization =
            [
                new HangfireDashboardAuthFilter(
                    app.Services.GetRequiredService<IConfiguration>(),
                    app.Services.GetRequiredService<IWebHostEnvironment>())
            ],
            DashboardTitle = "SheicobAnime Jobs"
        });
    }

    // ─── Endpoints ───────────────────────────────────────
    app.MapHealthEndpoints();
    app.MapSeriesEndpoints();
    app.MapEpisodeEndpoints();
    app.MapGenreEndpoints();
    app.MapMirrorEndpoints();
    app.MapAdminEndpoints();

    // ─── DB seeding ────────────────────────────────────────
    if (app.Environment.IsDevelopment())
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        await SeedData.SeedAsync(db);
    }
    else if (app.Environment.EnvironmentName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
    else if (app.Environment.IsProduction())
    {
        // Seed reference data (genres) — idempotent, safe to run every startup
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SeedData.SeedGenresAsync(db);
    }

    app.Run();
}
catch (Exception ex) when (!isTesting)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    if (!isTesting) Log.CloseAndFlush();
}

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program
{
    /// <summary>
    /// Converts a postgresql:// URI to ADO.NET connection string format.
    /// Hangfire.PostgreSql does not support URI format natively.
    /// </summary>
    internal static string NormalizePostgresConnectionString(string input)
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
}
