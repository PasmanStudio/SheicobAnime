using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace AnimeIndex.Api.Infrastructure.Logging;

/// <summary>
/// JsonFormatter que borra secretos del texto ya renderizado, como última red.
///
/// Origen (7-sep-2026): `ASPNETCORE_ENVIRONMENT` en el dashboard de Render tenía
/// pegado el `DATABASE_URL` entero detrás de la palabra "Production". Efecto
/// colateral: la línea `Hosting environment: {EnvName}` que ASP.NET escribe en
/// CADA arranque imprimía la contraseña de Supabase en texto plano en los logs
/// de Render — unas 80 veces por semana, durante semanas, sin que nadie la
/// estuviera logueando a propósito.
///
/// La causa se arregla en el env var, pero la lección es que basta con que un
/// secreto termine dentro de CUALQUIER string que se loguee. Este formatter
/// asume que va a volver a pasar y lo tapa: no confía en que quien loguea sepa
/// que lo que tiene en la mano es un secreto.
///
/// Es deliberadamente barato: un par de regex sobre el string final. Se ejecuta
/// una vez por línea de log, y el volumen del API es bajo (el `/health` cada 5s
/// de Render es lo más frecuente y no matchea nada).
/// </summary>
public sealed class RedactingJsonFormatter : ITextFormatter
{
    private readonly JsonFormatter _inner = new();

    // Pares clave=valor de una connection string de Npgsql/ADO.NET.
    private static readonly Regex KeyValueSecret = new(
        @"\b(Password|Pwd|User ID|Username)\s*=\s*[^;"" ]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Credenciales embebidas en una URI: postgres://user:pass@host, redis://...
    private static readonly Regex UriCredentials = new(
        @"(?<scheme>[a-z][a-z0-9+.\-]*://)(?<user>[^:@/\s""]+):(?<pass>[^@/\s""]+)@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);

        var text = buffer.ToString();
        text = KeyValueSecret.Replace(text, m => $"{m.Groups[1].Value}=***REDACTED***");
        text = UriCredentials.Replace(text, m => $"{m.Groups["scheme"].Value}{m.Groups["user"].Value}:***REDACTED***@");

        output.Write(text);
    }
}
