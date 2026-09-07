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
using AnimeIndex.Api.Infrastructure.Logging;
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
        .WriteTo.Console(new RedactingJsonFormatter())
        .CreateBootstrapLogger();
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Guarda del nombre de entorno ────────────────────
    //
    // El 7-sep-2026 se encontró `ASPNETCORE_ENVIRONMENT` con 223 caracteres en
    // Render: el literal "Production" con el `DATABASE_URL` entero pegado atrás
    // (un pegado mal hecho en el dashboard). Nada falló de forma visible, y ese
    // es el problema: `IsProduction()` pasó a devolver false, así que durante
    // semanas se saltearon EN SILENCIO la validación de env vars de acá abajo y
    // el bloque de migraciones de arranque, y `appsettings.Production.json`
    // nunca se cargó (ASP.NET buscaba `appsettings.ProductionHost=aws-1...json`).
    //
    // Un `IsProduction()` que devuelve false por un typo no debe ser silencioso.
    // Se detecta la corrupción, se grita en los logs, y se sigue tratando al
    // entorno como Production para que el comportamiento sea el correcto — NO se
    // tira una excepción a propósito: un fallo fatal en el arranque por un env
    // var mal puesto es exactamente el bucle de reinicios del PR #164.
    var rawEnvName = builder.Environment.EnvironmentName;
    var envNameIsCorrupted =
        rawEnvName.Contains(';') || rawEnvName.Contains('=') || rawEnvName.Length > 32;

    if (envNameIsCorrupted)
    {
        Log.Error(
            "ASPNETCORE_ENVIRONMENT está corrupto: {Length} caracteres, empieza con {Prefix}. " +
            "Casi seguro tiene otra variable pegada atrás. Efecto: IsProduction()/IsDevelopment() " +
            "devuelven false y appsettings.{{Environment}}.json no se carga. " +
            "Corregilo en el dashboard de Render (debe ser exactamente 'Production')",
            rawEnvName.Length,
            rawEnvName[..Math.Min(12, rawEnvName.Length)]);
    }

    // Usar SIEMPRE esto en vez de builder.Environment.IsProduction(): tolera el
    // env var corrupto en lugar de desactivar medio arranque sin avisar.
    var isProduction =
        builder.Environment.IsProduction()
        || (envNameIsCorrupted && rawEnvName.StartsWith("Production", StringComparison.OrdinalIgnoreCase));

    // ─── Production env var validation (fail fast) ───────
    if (isProduction)
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
            .WriteTo.Console(new RedactingJsonFormatter()));
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
                // Nombre saneado, no el crudo: si el env var vuelve a venir con
                // una connection string pegada, no la mandamos a un tercero.
                o.Environment = envNameIsCorrupted && isProduction
                    ? "Production"
                    : builder.Environment.EnvironmentName;
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

        // Pool y timeouts explícitos, sobre CUALQUIER formato de entrada.
        // Antes solo se aplicaban al normalizar una URI, así que un DATABASE_URL
        // ya en formato ADO.NET quedaba con los defaults de Npgsql (pool 100,
        // idle lifetime 300s) contra un pooler de Supabase free que admite ~15
        // conexiones — receta para timeouts de conexión bajo ráfaga.
        var maxPoolSize = builder.Configuration.GetValue<int?>("DB_MAX_POOL_SIZE") ?? 10;
        connectionString = ApplyPoolSettings(connectionString, maxPoolSize, "sheicobanime-api");

        // Hangfire abre sus propias conexiones. Npgsql poolea POR connection
        // string, así que compartir la string significaba compartir el pool: el
        // polling de Hangfire consumía slots que necesitaban los requests. Con
        // un Application Name distinto obtiene su propio pool, chico y aislado.
        var hangfireConnectionString = ApplyPoolSettings(
            connectionString, maxPoolSize: 4, applicationName: "sheicobanime-hangfire");

        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                // 2 reintentos y no 3, con backoff corto: cada reintento retiene
                // el slot del pool. Bajo saturación, reintentar agresivamente
                // empeora la congestión en vez de aliviarla.
                npgsqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 2,
                    maxRetryDelay: TimeSpan.FromSeconds(2),
                    errorCodesToAdd: null);
                // 15s en vez de 30: el frontend aborta el fetch a los 12s, así
                // que una query de más de 15s no le sirve a nadie y solo bloquea
                // una conexión.
                npgsqlOptions.CommandTimeout(15);
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
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(hangfireConnectionString)));
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
    // OJO con el límite global: el frontend corre en Cloudflare Workers y todo
    // el tráfico SSR llega desde un puñado de IPs de salida de Cloudflare, así
    // que TODOS los visitantes caen en la misma partición. Con el viejo límite
    // de 60/min eso era 1 req/s para el sitio entero: bajo cualquier pico real
    // los usuarios legítimos comían 429 y la página se rompía. Configurable por
    // env para poder ajustarlo sin deploy.
    var globalRateLimit = builder.Configuration.GetValue<int?>("RATE_LIMIT_PER_MINUTE") ?? 240;
    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetSlidingWindowLimiter(
                GetClientIp(context),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = globalRateLimit,
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

    // ─── In-memory cache (L1 for the two-tier RedisCacheService) ──
    builder.Services.AddMemoryCache();

    var app = builder.Build();

    // ─── Forwarded Headers (Railway / Cloudflare reverse proxy) ──
    // Railway terminates TLS at its edge and forwards requests to the container
    // over plain HTTP. Without this, HttpContext.Request.Scheme is "http" and any
    // absolute URL the API builds from the request (canonical links, redirects)
    // gets an http:// base, causing Mixed Content errors in the browser.
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

    // ─── Cancelaciones del cliente ≠ errores del servidor ────────────────
    // Cuando el Worker de Cloudflare (o el navegador) corta el request, EF Core
    // lanza OperationCanceledException/TaskCanceledException y el pipeline la
    // convertía en 500: ruido de Errores en los logs, en Sentry, y métricas de
    // fallas infladas que hacían parecer caído un API que solo tenía clientes
    // impacientes. 499 ("client closed request") es lo que corresponde, y al no
    // ser >=500 Serilog lo loguea como Information en vez de Error.
    // Va DESPUÉS de Serilog/Sentry a propósito: así los ve como un 499 limpio.
    app.Use(async (context, next) =>
    {
        try
        {
            await next();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 499;
            }
        }
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
    app.MapAniListEndpoints();
    app.MapMirrorEndpoints();
    app.MapProgressEndpoints();
    app.MapEpisodeRatingEndpoints();
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
    else if (isProduction)
    {
        // Migración + seed al arrancar, PERO nunca fatal.
        //
        // Antes esto era `await db.Database.MigrateAsync()` pelado: si la DB
        // estaba saturada o lenta en ese instante (justo lo que pasa cuando
        // Render reinicia la instancia en plena ráfaga de crawlers), la
        // excepción subía al catch de más afuera, se logueaba "Application
        // terminated unexpectedly" y el proceso moría — el mail de Render
        // "Application exited early". Y como al reiniciar la DB seguía
        // saturada, el arranque volvía a fallar: bucle de reinicios.
        //
        // Las migraciones en prod ya están aplicadas en el 99% de los deploys,
        // así que un fallo transitorio acá no debe impedir que el API sirva
        // (tiene cache y se recupera solo). Se reintenta con backoff y, si aun
        // así falla, se loguea el error y se arranca igual.
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync();
                // Seed reference data (genres) — idempotent, safe to run every startup
                await SeedData.SeedGenresAsync(db);
                // Invalidate genres cache so stale empty results don't persist
                var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();
                await cache.RemoveAsync("genres:all");
                break;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    Log.Error(ex,
                        "Migración/seed de arranque falló tras {Attempts} intentos. " +
                        "El API arranca igual: las migraciones probablemente ya estén " +
                        "aplicadas y matar el proceso solo provocaría un bucle de reinicios",
                        maxAttempts);
                    break;
                }

                var delay = TimeSpan.FromSeconds(3 * attempt);
                Log.Warning(ex,
                    "Migración/seed de arranque falló (intento {Attempt}/{Attempts}), " +
                    "reintentando en {Delay}s", attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay);
            }
        }
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

        return $"Host={host};Port={port};Database={database};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true";
    }

    /// <summary>
    /// Fija pool y timeouts sobre una connection string de Postgres ya normalizada.
    ///
    /// Contexto del incidente de ago-2026: ráfagas de crawlers sobre el long tail
    /// de episodios abrían decenas de requests simultáneos; el pool se quedaba sin
    /// conexiones libres, abrir una nueva contra el pooler de Supabase tardaba
    /// segundos y los requests morían con TaskCanceledException dentro de
    /// NpgsqlConnector.ConnectAsync. Las claves que importan:
    ///
    /// - Timeout: 8s para ABRIR (default 15). Fallar rápido evita que se apilen
    ///   requests esperando una conexión que no va a llegar.
    /// - Connection Idle Lifetime: 60s (default 300). El pooler de Supabase corta
    ///   conexiones ociosas por su cuenta; si el pool las retiene más tiempo que
    ///   el server, entrega conexiones muertas y el request falla al primer uso.
    /// - Keepalive: 30s para que el pooler no considere ociosa una conexión viva.
    /// - Application Name: separa pools (Npgsql poolea por connection string) y
    ///   hace visible en pg_stat_activity quién consume conexiones.
    /// </summary>
    internal static string ApplyPoolSettings(string connectionString, int maxPoolSize, string applicationName)
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MaxPoolSize = Math.Clamp(maxPoolSize, 2, 50),
            MinPoolSize = 0,
            Timeout = 8,
            ConnectionIdleLifetime = 60,
            ConnectionPruningInterval = 10,
            KeepAlive = 30,
            ApplicationName = applicationName,
        };
        return b.ConnectionString;
    }
}
