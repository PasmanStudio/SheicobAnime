using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using AnimeIndex.Api;
using AnimeIndex.Api.Data;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.DTOs.Admin;
using AnimeIndex.Api.Endpoints;
using AnimeIndex.Api.Infrastructure;
using AnimeIndex.Api.Infrastructure.Auth;
using AnimeIndex.Api.Infrastructure.Cache;
using AnimeIndex.Api.Infrastructure.Resolvers;
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
                // OperationCanceledException = client closed the request (navigated away / tab closed).
                // This is expected behavior, not an application error — filter it out to avoid noise.
                o.SetBeforeSend((sentryEvent, _) =>
                    sentryEvent.Exception is OperationCanceledException ? null : sentryEvent);
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
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
                npgsqlOptions.CommandTimeout(30);
            });
            // Raw-SQL migrations (AddAuthTables, AddDiscordPosts, AddUserWatchlist) don't update
            // the EF snapshot — suppress the PendingModelChangesWarning so `database update` runs.
            options.ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });

        // ─── Hangfire (client + dashboard only — NO server/workers) ───
        // The scraper service owns all Hangfire workers and recurring jobs.
        // Running AddHangfireServer here causes the API's RecurringJobScheduler
        // to pick up scraper jobs it can't deserialize (missing AnimeIndex.Scraper
        // assembly), which permanently kills the recurring schedule after 5 retries.
        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString)));
    }

    // ─── Redis / Cache ───────────────────────────────────
    if (!isTesting)
    {
        var redisConnection = builder.Configuration.GetConnectionString("Redis")
            ?? builder.Configuration["REDIS_URL"]
            ?? "localhost:6379";

        // AbortOnConnectFail=false so the API still boots when Redis is unreachable
        // or over-quota — the cache layer degrades to Postgres instead of crashing
        // startup. (Hardened after the June 2026 Upstash quota incident.)
        var redisOptions = ConfigurationOptions.Parse(redisConnection);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectRetry = 3;

        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisOptions));
        builder.Services.AddSingleton<ICacheService, RedisCacheService>();
    }

    // ─── Mapster ─────────────────────────────────────────
    MappingConfig.RegisterMappings();

    // ─── FluentValidation ────────────────────────────────
    builder.Services.AddScoped<IValidator<CreateScrapeJobRequest>, CreateScrapeJobValidator>();
    builder.Services.AddScoped<IValidator<CreateBlockedSlugRequest>, CreateBlockedSlugValidator>();
    builder.Services.AddScoped<IValidator<CreateBackfillRequest>, CreateBackfillValidator>();

    // ─── Rate Limiting ───────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetSlidingWindowLimiter(
                GetClientIp(context),
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
                GetClientIp(context),
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
                  .AllowAnyMethod()
                  .AllowCredentials();
        });
    });

    // ─── Auth filters (transient for DI) ─────────────────
    builder.Services.AddTransient<AdminKeyEndpointFilter>();

    // ─── HTTP clients ─────────────────────────────────────
    builder.Services.AddHttpClient("probe", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
    });
    builder.Services.AddScoped<MirrorProbeService>();

    // ─── VAST proxy client ─────────────────────────────────
    builder.Services.AddHttpClient("vast", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
    });

    // ─── Resolvers (Phase 20) ─────────────────────────────
    builder.Services.AddMemoryCache();
    builder.Services.AddHttpClient("resolver", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(15);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Higher connection pool for concurrent proxy streaming (default is 2 per host).
        MaxConnectionsPerServer = 64,
        // Keep TCP connections alive between segment requests.
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = true,
    });
    builder.Services.AddSingleton<IHosterResolver, Mp4UploadResolver>();
    builder.Services.AddSingleton<IHosterResolver, VidhideResolver>();
    builder.Services.AddSingleton<IHosterResolver, OkruResolver>();
    builder.Services.AddSingleton<IHosterResolver, StreamwishResolver>();
    builder.Services.AddSingleton<IHosterResolver, VoeResolver>();
    builder.Services.AddSingleton<IHosterResolver, MixdropResolver>();
    builder.Services.AddSingleton<IHosterResolver, MediafireResolver>();
    builder.Services.AddSingleton<ResolverRegistry>();

    // ─── Streaming proxy (signed HMAC) ──────────────────
    builder.Services.AddSingleton<AnimeIndex.Api.Infrastructure.Proxy.ProxyUrlSigner>();

    var app = builder.Build();

    // ─── Forwarded Headers (Railway / Cloudflare reverse proxy) ──
    // Railway terminates TLS at its edge and forwards requests to the container
    // over plain HTTP. Without this, HttpContext.Request.Scheme is "http" and
    // proxy URLs returned by /mirrors/{id}/resolve have an http:// base, causing
    // Mixed Content errors in the browser.
    // ─── Forwarded Headers (Railway / Cloudflare reverse proxy) ──
    // Railway terminates TLS at its edge and forwards requests to the container
    // over plain HTTP. Without this, HttpContext.Request.Scheme is "http" and
    // proxy URLs returned by /mirrors/{id}/resolve have an http:// base, causing
    // Mixed Content errors in the browser.
    {
        var fhOpts = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        fhOpts.KnownNetworks.Clear();
        fhOpts.KnownProxies.Clear();
        app.UseForwardedHeaders(fhOpts);
    }

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
            diagnosticContext.Set("ClientIp", GetClientIp(httpContext));
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

    // Device-id cookie (anonymous viewer identifier for watch_progress)
    app.UseMiddleware<DeviceIdMiddleware>();

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
    app.MapProgressEndpoints();
    app.MapAdminEndpoints();
    app.MapProxyEndpoints();
    app.MapVastProxyEndpoints();

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
        // Apply pending EF migrations on startup (safe — idempotent)
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        // Seed reference data (genres) — idempotent, safe to run every startup
        await SeedData.SeedGenresAsync(db);
        // Invalidate genres cache so stale empty results don't persist
        var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();
        await cache.RemoveAsync("genres:all");
    }

    app.Run();
}
catch (HostAbortedException)
{
    // Expected when EF Core design-time tools (dotnet ef) build the host then abort it.
    // Not an error — suppress the misleading Fatal log.
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
    /// Resolves the real client IP for rate-limiting and logging.
    ///
    /// On Render, HttpContext.Connection.RemoteIpAddress is an internal load-balancer
    /// address (10.x), not the visitor — so partitioning the rate limiter by it lumped
    /// everyone (including scrapers) into a handful of useless buckets. The original
    /// client is the LEFTMOST entry of X-Forwarded-For; Render appends downstream hops
    /// to the right, and ASP.NET's ForwardedHeaders only strips from the right, so the
    /// leftmost survives. Falls back to RemoteIpAddress when the header is absent.
    /// </summary>
    internal static string GetClientIp(HttpContext context)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (first.Length > 0 && !string.IsNullOrWhiteSpace(first[0]))
                return first[0];
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

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

        return $"Host={host};Port={port};Database={database};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=8";
    }
}
