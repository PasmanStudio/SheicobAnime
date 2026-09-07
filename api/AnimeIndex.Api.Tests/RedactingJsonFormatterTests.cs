using AnimeIndex.Api.Infrastructure.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace AnimeIndex.Api.Tests;

/// <summary>
/// El formatter existe por un incidente real: `ASPNETCORE_ENVIRONMENT` en Render
/// tenía el `DATABASE_URL` pegado atrás, así que la línea "Hosting environment:
/// {EnvName}" que ASP.NET escribe en cada arranque publicó la contraseña de
/// Supabase en los logs durante semanas. Estos tests fijan que ningún secreto
/// con esa forma vuelva a salir por el log, venga de donde venga.
/// </summary>
public class RedactingJsonFormatterTests
{
    private static string Format(string template, params object[] args)
    {
        var parser = new MessageTemplateParser();
        var properties = args
            .Select((a, i) => new LogEventProperty($"p{i}", new ScalarValue(a)))
            .ToList();

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            parser.Parse(template),
            properties);

        var writer = new StringWriter();
        new RedactingJsonFormatter().Format(logEvent, writer);
        return writer.ToString();
    }

    [Fact]
    public void Redacts_Password_From_AdoNet_ConnectionString()
    {
        // Exactamente la forma que se filtró en producción.
        const string leaked =
            "ProductionHost=aws-1-us-east-1.pooler.supabase.com;Port=5432;Database=postgres;"
            + "Username=postgres.abcdefg;Password=SuperSecret123;SSL Mode=Require";

        var output = Format("Hosting environment: {p0}", leaked);

        Assert.DoesNotContain("SuperSecret123", output);
        Assert.DoesNotContain("postgres.abcdefg", output);
        Assert.Contains("REDACTED", output);
        // El resto de la línea tiene que seguir siendo útil para diagnosticar.
        Assert.Contains("pooler.supabase.com", output);
    }

    [Fact]
    public void Redacts_Credentials_From_Uri_Style_ConnectionString()
    {
        var output = Format("conectando a {p0}", "postgresql://admin:hunter2@db.example.com:5432/postgres");

        Assert.DoesNotContain("hunter2", output);
        Assert.Contains("REDACTED", output);
        Assert.Contains("db.example.com", output);
    }

    [Fact]
    public void Redacts_Redis_Url_Credentials()
    {
        var output = Format("cache en {p0}", "redis://default:r3d1sPass@fly-cache.upstash.io:6379");

        Assert.DoesNotContain("r3d1sPass", output);
        Assert.Contains("fly-cache.upstash.io", output);
    }

    [Fact]
    public void Leaves_Normal_Log_Lines_Untouched()
    {
        var output = Format("HTTP {p0} {p1} responded {p2}", "GET", "/health", 200);

        Assert.Contains("/health", output);
        Assert.Contains("200", output);
        Assert.DoesNotContain("REDACTED", output);
    }
}
