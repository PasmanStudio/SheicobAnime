using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// La imagen de lo que se NOMBRA, para cualquier tema, sin nombres escritos a mano:
///   • póster de una serie o temporada → AniList (portada); si no es anime
///     (un videojuego, una película), la imagen principal de su artículo en Wikipedia;
///   • logo de un estudio, empresa o plataforma → el campo "logo" de la infobox
///     de Wikipedia en inglés (los SVG se piden ya rasterizados);
///   • personaje → AniList (búsqueda de personaje, no solo los de una serie:
///     así aparecen los de una temporada nueva).
/// Todo queda en caché en la carpeta del video (mentions/), así un re-render no
/// vuelve a pedir nada. Wikimedia limita a los clientes sin registro: un pedido
/// por segundo y, ante un 429, se espera lo que diga y se reintenta.
/// </summary>
public sealed partial class MentionImageService(IHttpClientFactory httpFactory, ILogger<MentionImageService> logger)
{
    private static readonly TimeSpan WikiInterval = TimeSpan.FromSeconds(1.1);
    private DateTime _nextWikiAt = DateTime.MinValue;
    private DateTime _nextAniListAt = DateTime.MinValue;
    private readonly Dictionary<string, string?> _memo = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> PosterAsync(string name, string dir, CancellationToken ct) =>
        CachedAsync("poster", name, dir, ct, async () =>
            await AniListPosterAsync(name, ct) ?? await WikipediaImageAsync(name, logo: false, ct));

    public async Task<string?> LogoAsync(string name, string dir, CancellationToken ct)
    {
        // El canal se nombra a sí mismo ("Sheicob Anime está aquí…"): su logo es nuestro.
        if (string.Concat(ShotTiming.Tokens(name)) == "sheicobanime")
        {
            var own = Path.Combine(dir, "mentions", "logo_sheicob-anime.png");
            if (!File.Exists(own))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(own)!);
                await using var res = typeof(MentionImageService).Assembly.GetManifestResourceStream("AnimeIndex.Scraper.Resources.sheicob-logo.png");
                if (res is null) return null;
                await using var file = File.Create(own);
                await res.CopyToAsync(file, ct);
            }
            return own;
        }
        return await CachedAsync("logo", name, dir, ct, () => WikipediaImageAsync(name, logo: true, ct));
    }

    /// <summary>Foto o imagen principal del artículo de Wikipedia (personas reales, lugares, obras que no son anime).</summary>
    public Task<string?> PhotoAsync(string name, string dir, CancellationToken ct) =>
        CachedAsync("photo", name, dir, ct, async () =>
            (await WikipediaKindAsync(name, ct) is "person" or "place" or "work" ? await WikipediaImageAsync(name, logo: false, ct) : null)
            ?? await AniListStaffImageAsync(name, ct));

    /// <summary>Foto del staff en AniList (directores, guionistas, músicos), solo con el nombre exacto.</summary>
    private async Task<string?> AniListStaffImageAsync(string name, CancellationToken ct)
    {
        using var doc = await AniListAsync("query ($s: String) { Staff(search: $s) { name { full } image { large } } }", WithoutAccents(name), ct);
        if (doc is null || !doc.RootElement.GetProperty("data").TryGetProperty("Staff", out var st) || st.ValueKind != JsonValueKind.Object)
            return null;
        if (string.Concat(ShotTiming.Tokens(st.GetProperty("name").GetProperty("full").GetString() ?? "")) != string.Concat(ShotTiming.Tokens(name)))
            return null;
        var url = st.GetProperty("image").GetProperty("large").GetString();
        return url is null || url.Contains("default.jpg", StringComparison.Ordinal) ? null : url;
    }

    // ── Clasificación de nombres ────────────────────────────────────────────

    private Dictionary<string, string>? _entities;
    private string? _entitiesPath;

    /// <summary>
    /// ¿Qué es este nombre? "series" (anime en AniList, título exacto), "character"
    /// (personaje de AniList con el nombre completo exacto), o por la infobox de su
    /// artículo en Wikipedia: "company", "work" (videojuego, película, libro, disco),
    /// "person", "place", "award"; si no hay artículo, el staff de AniList también es "person". "" si no se sabe. Se guarda en mentions/entities.json.
    /// </summary>
    public async Task<string> ClassifyAsync(string name, string dir, CancellationToken ct)
    {
        if (_entities is null)
        {
            _entitiesPath = Path.Combine(dir, "mentions", "entities.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_entitiesPath)!);
            _entities = File.Exists(_entitiesPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_entitiesPath, ct)) ?? []
                : [];
        }
        if (_entities.TryGetValue(name, out var known)) return known;
        if (string.Concat(ShotTiming.Tokens(name)) == "sheicobanime") return _entities[name] = "company";

        var kind = "";
        try
        {
            if (await AniListExactSeriesAsync(name, ct)) kind = "series";
            else if (name.Contains(' ') && await AniListExactCharacterAsync(name, ct)) kind = "character";
            else kind = await WikipediaKindAsync(name, ct);
            // Sin artículo: ¿es alguien del staff de un anime (director, guionista, músico)?
            if (kind == "" && name.Contains(' ') && await AniListStaffImageAsync(name, ct) is not null) kind = "person";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("No se pudo clasificar '{Name}': {Msg}", name, ex.Message);
            return ""; // sin guardar: se reintenta en la próxima corrida
        }
        _entities[name] = kind;
        await File.WriteAllTextAsync(_entitiesPath!, JsonSerializer.Serialize(_entities, VideoPlan.Json), ct);
        return kind;
    }

    /// <summary>Nombres completos de los personajes de una serie de AniList (para reconocer "Avida" o "Talia" sueltos).</summary>
    public async Task<List<string>> SeriesCharactersAsync(string series, CancellationToken ct)
    {
        try
        {
            using var doc = await AniListAsync(
                "query ($s: String) { Media(search: $s, type: ANIME) { characters(perPage: 50, sort: [ROLE, FAVOURITES_DESC]) { nodes { name { full } } } } }",
                series, ct);
            if (doc is null || doc.RootElement.GetProperty("data").GetProperty("Media").ValueKind != JsonValueKind.Object) return [];
            return doc.RootElement.GetProperty("data").GetProperty("Media").GetProperty("characters").GetProperty("nodes")
                .EnumerateArray().Select(n => n.GetProperty("name").GetProperty("full").GetString() ?? "")
                .Where(n => n.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Personajes de '{Series}' no disponibles: {Msg}", series, ex.Message);
            return [];
        }
    }

    private async Task<bool> AniListExactSeriesAsync(string name, CancellationToken ct)
    {
        using var doc = await AniListAsync(
            "query ($s: String) { Media(search: $s, type: ANIME) { title { romaji english } synonyms } }", name, ct);
        if (doc is null || !doc.RootElement.GetProperty("data").TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Object)
            return false;
        var titles = new List<string?> { media.GetProperty("title").GetProperty("romaji").GetString(), media.GetProperty("title").GetProperty("english").GetString() };
        titles.AddRange(media.GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()));
        var wanted = string.Concat(ShotTiming.Tokens(name));
        return titles.OfType<string>().Any(t => string.Concat(ShotTiming.Tokens(t)) == wanted);
    }

    private async Task<bool> AniListExactCharacterAsync(string name, CancellationToken ct)
    {
        using var doc = await AniListAsync("query ($s: String) { Character(search: $s) { name { full } } }", WithoutAccents(name), ct);
        if (doc is null || !doc.RootElement.GetProperty("data").TryGetProperty("Character", out var c) || c.ValueKind != JsonValueKind.Object)
            return false;
        return string.Concat(ShotTiming.Tokens(c.GetProperty("name").GetProperty("full").GetString() ?? "")) == string.Concat(ShotTiming.Tokens(name));
    }

    /// <summary>Tipo de cosa según la infobox del artículo en inglés con ese título exacto (o su redirección).</summary>
    private async Task<string> WikipediaKindAsync(string name, CancellationToken ct)
    {
        const string api = "https://en.wikipedia.org/w/api.php?format=json&formatversion=2&";
        using var q = await WikiJsonAsync(api + "action=query&redirects=1&prop=pageprops&titles=" + Uri.EscapeDataString(name), ct);
        var page = q.RootElement.GetProperty("query").GetProperty("pages")[0];
        if (page.TryGetProperty("missing", out _)) return "";
        if (page.TryGetProperty("pageprops", out var props) && props.TryGetProperty("disambiguation", out _)) return "";
        var title = page.GetProperty("title").GetString() ?? "";
        // Una redirección a otra cosa ("Militech" → el juego) no es el artículo de ESE nombre.
        if (!TitleIsName(name, title)) return "";
        using var parse = await WikiJsonAsync(api + "action=parse&prop=wikitext&section=0&page=" + Uri.EscapeDataString(title), ct);
        var wikitext = parse.RootElement.GetProperty("parse").GetProperty("wikitext").GetString() ?? "";
        return InfoboxKind(wikitext);
    }

    /// <summary>¿El artículo (después de redirecciones) es de ese nombre? "CD Projekt" → "CD Projekt Red" sí; "Militech" → "Cyberpunk 2077" no. Público para tests.</summary>
    public static bool TitleIsName(string name, string title)
    {
        var wanted = ShotTiming.Tokens(name);
        var got = ShotTiming.Tokens(Regex.Replace(title, @"\s*\(.*\)$", ""));
        return wanted.Count > 0 && wanted.All(got.Contains) && got.Count - wanted.Count <= 1;
    }

    /// <summary>"{{Infobox company" → "company", "{{Infobox video game" → "work", etc. Público para tests.</summary>
    public static string InfoboxKind(string wikitext)
    {
        var m = Regex.Match(wikitext, @"\{\{\s*Infobox[ _]+([^\n|}]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        var type = m.Groups[1].Value.Trim().ToLowerInvariant();
        if (Regex.IsMatch(type, "award|prize|honou?r")) return "award";
        if (Regex.IsMatch(type, "company|organi[sz]ation|corporation|brand|website|channel|network|publisher|record label|studio|broadcast|service|streaming"))
            return "company";
        if (Regex.IsMatch(type, "video game|film|television|book|album|song|single|comic|manga|animanga|media franchise|game|novel|play"))
            return "work";
        if (Regex.IsMatch(type, "person|artist|musician|writer|director|animator|actor|voice|creator|officeholder|scientist|athlete|youtuber|comedian|model"))
            return "person";
        if (Regex.IsMatch(type, "settlement|city|country|place|building|venue|island|district|region"))
            return "place";
        return "";
    }

    public Task<string?> CharacterAsync(string name, string dir, CancellationToken ct) =>
        CachedAsync("character", name, dir, ct, async () =>
            // AniList guarda los nombres sin tildes ("David Martinez"): con tilde no encuentra nada.
            await AniListCharacterAsync(name, ct)
            ?? (WithoutAccents(name) is var plain && plain != name ? await AniListCharacterAsync(plain, ct) : null));

    /// <summary>"David Martínez" → "David Martinez". Público para tests.</summary>
    public static string WithoutAccents(string s) =>
        new(s.Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());

    /// <summary>
    /// URL → archivo local. La caché en disco guarda también los "no encontrado"
    /// (archivo .none) para no repetir la búsqueda en cada render.
    /// </summary>
    private async Task<string?> CachedAsync(string kind, string name, string dir, CancellationToken ct, Func<Task<string?>> findUrl)
    {
        var key = $"{kind}:{name.Trim()}";
        if (_memo.TryGetValue(key, out var memo)) return memo;
        var folder = Path.Combine(dir, "mentions");
        Directory.CreateDirectory(folder);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..10];
        var stem = Path.Combine(folder, $"{kind}_{Slug(name)}_{hash}");
        var existing = Directory.GetFiles(folder, Path.GetFileName(stem) + ".*").FirstOrDefault();
        if (existing is not null)
            return _memo[key] = existing.EndsWith(".none", StringComparison.Ordinal) ? null : existing;

        string? path = null;
        try
        {
            var url = await findUrl();
            if (url is not null)
            {
                var bytes = await GetBytesAsync(url, ct);
                var ext = ImageExtension(bytes);
                if (ext is not null)
                {
                    path = stem + ext;
                    await File.WriteAllBytesAsync(path, bytes, ct);
                }
                else logger.LogWarning("{Kind} '{Name}': {Url} no devolvió una imagen", kind, name, url);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("{Kind} '{Name}' no disponible: {Msg}", kind, name, ex.Message);
            return _memo[key] = null; // error de red: no se guarda el "no encontrado"
        }
        if (path is null) await File.WriteAllTextAsync(stem + ".none", "", ct);
        logger.LogInformation("{Kind} '{Name}': {Result}", kind, name, path is null ? "sin imagen" : Path.GetFileName(path));
        return _memo[key] = path;
    }

    // ── AniList ─────────────────────────────────────────────────────────────

    private async Task<string?> AniListPosterAsync(string name, CancellationToken ct)
    {
        using var doc = await AniListAsync(
            "query ($s: String) { Media(search: $s, type: ANIME) { title { romaji english } synonyms coverImage { extraLarge } } }",
            name, ct);
        if (doc is null || !doc.RootElement.GetProperty("data").TryGetProperty("Media", out var media)
            || media.ValueKind != JsonValueKind.Object) return null;
        var titles = new List<string?>
        {
            media.GetProperty("title").GetProperty("romaji").GetString(),
            media.GetProperty("title").GetProperty("english").GetString(),
        };
        titles.AddRange(media.GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()));
        // AniList devuelve "lo más parecido": "Cyberpunk 2077" no es un anime y
        // traería Edgerunners. Solo vale si el título coincide de verdad.
        if (!titles.OfType<string>().Any(t => SameTitle(name, t))) return null;
        return media.GetProperty("coverImage").GetProperty("extraLarge").GetString();
    }

    private async Task<string?> AniListCharacterAsync(string name, CancellationToken ct)
    {
        using var doc = await AniListAsync(
            "query ($s: String) { Character(search: $s) { name { full } image { large } } }", name, ct);
        if (doc is null || !doc.RootElement.GetProperty("data").TryGetProperty("Character", out var c)
            || c.ValueKind != JsonValueKind.Object) return null;
        var url = c.GetProperty("image").GetProperty("large").GetString();
        return url is null || url.Contains("default.jpg", StringComparison.Ordinal) ? null : url;
    }

    private async Task<JsonDocument?> AniListAsync(string query, string search, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { query, variables = new { s = search } });
        for (var attempt = 1; ; attempt++)
        {
            // AniList: ~30 pedidos por minuto en la práctica; uno cada 2 s y, ante un 429, esperar lo que pida.
            var wait = _nextAniListAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _nextAniListAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            using var resp = await httpFactory.CreateClient("youtube-research").PostAsync("https://graphql.anilist.co",
                new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null; // AniList: "Not Found" = sin resultado
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
            {
                _nextAniListAt = DateTime.UtcNow + (resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30));
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
    }

    /// <summary>
    /// ¿El título encontrado es el pedido? Iguales sin signos, o el encontrado
    /// contiene todas las palabras del pedido y las de más no son un número de temporada ("2", "II"). Público para tests.
    /// </summary>
    public static bool SameTitle(string wanted, string found)
    {
        var a = ShotTiming.Tokens(wanted);
        var b = ShotTiming.Tokens(found);
        if (a.Count == 0 || b.Count == 0) return false;
        if (string.Concat(a) == string.Concat(b)) return true;
        return a.All(b.Contains) && b.Where(w => !a.Contains(w)).All(w => !w.All(char.IsDigit) && !RomanNumeral().IsMatch(w));
    }

    // ── Wikipedia ───────────────────────────────────────────────────────────

    /// <summary>
    /// Artículo de Wikipedia en inglés → su logo (campo "logo" de la infobox) o
    /// su imagen principal. Busca el artículo por título exacto (con redirecciones)
    /// y, si no existe, por búsqueda.
    /// </summary>
    private async Task<string?> WikipediaImageAsync(string name, bool logo, CancellationToken ct)
    {
        const string api = "https://en.wikipedia.org/w/api.php?format=json&formatversion=2&";
        using var exact = await WikiJsonAsync(api + "action=query&redirects=1&titles=" + Uri.EscapeDataString(name), ct);
        var page = exact.RootElement.GetProperty("query").GetProperty("pages")[0];
        var title = page.TryGetProperty("missing", out _) ? null : page.GetProperty("title").GetString();
        // Solo el artículo de ESE nombre: con la búsqueda, "Kai Ikarashi" (sin artículo)
        // traía la imagen del artículo de la serie que dirige.
        if (title is null || !TitleIsName(name, title)) return null;

        string? file = null;
        using (var parse = await WikiJsonAsync(api + "action=parse&prop=wikitext&section=0&redirects=1&page=" + Uri.EscapeDataString(title), ct))
        {
            var wikitext = parse.RootElement.GetProperty("parse").GetProperty("wikitext").GetString() ?? "";
            file = InfoboxImage(wikitext, logo ? "logo" : "image") ?? (logo ? null : InfoboxImage(wikitext, "cover"));
        }
        if (file is null)
        {
            // Sin infobox: la imagen principal del artículo (también la no libre).
            using var pi = await WikiJsonAsync(api + "action=query&prop=pageimages&piprop=name&pilicense=any&titles=" + Uri.EscapeDataString(title), ct);
            var p = pi.RootElement.GetProperty("query").GetProperty("pages")[0];
            file = p.TryGetProperty("pageimage", out var img) ? img.GetString() : null;
            if (file is null) return null;
        }

        using var info = await WikiJsonAsync(api + "action=query&prop=imageinfo&iiprop=url&iiurlwidth=1000&titles=" +
            Uri.EscapeDataString("File:" + file), ct);
        var ip = info.RootElement.GetProperty("query").GetProperty("pages")[0];
        if (!ip.TryGetProperty("imageinfo", out var ii)) return null;
        var first = ii[0];
        return first.TryGetProperty("thumburl", out var thumb) ? thumb.GetString() : first.GetProperty("url").GetString();
    }

    /// <summary>"| logo = Trigger Logo.svg" (o [[File:...]]) de la infobox → nombre del archivo. Público para tests.</summary>
    public static string? InfoboxImage(string wikitext, string field)
    {
        var m = Regex.Match(wikitext,
            $@"^\s*\|\s*{Regex.Escape(field)}\s*=\s*(?:\[\[)?(?:(?:File|Image|Archivo):)?\s*([^|\]\n{{}}]+?\.(?:svg|png|jpe?g|gif|webp))",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private async Task<JsonDocument> WikiJsonAsync(string url, CancellationToken ct) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(await GetBytesAsync(url, ct)));

    /// <summary>GET con el ritmo que pide Wikimedia (1 por segundo, Retry-After en 429).</summary>
    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        var wiki = url.Contains("wikipedia.org", StringComparison.Ordinal) || url.Contains("wikimedia.org", StringComparison.Ordinal);
        var http = httpFactory.CreateClient("youtube-research");
        for (var attempt = 1; ; attempt++)
        {
            if (wiki)
            {
                var wait = _nextWikiAt - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _nextWikiAt = DateTime.UtcNow + WikiInterval;
            }
            using var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
            {
                var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                if (retry > TimeSpan.FromSeconds(60)) retry = TimeSpan.FromSeconds(60);
                logger.LogInformation("Wikimedia pide esperar {Secs:F0}s", retry.TotalSeconds);
                _nextWikiAt = DateTime.UtcNow + retry;
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
    }

    /// <summary>Extensión según los primeros bytes; null si no es una imagen (p. ej. una página de desafío).</summary>
    public static string? ImageExtension(byte[] b) =>
        b.Length < 12 ? null
        : b[0] == 0xFF && b[1] == 0xD8 ? ".jpg"
        : b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? ".png"
        : b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' ? ".webp"
        : b[0] == 'G' && b[1] == 'I' && b[2] == 'F' ? ".gif"
        : null;

    [GeneratedRegex(@"^(?:i{1,3}|iv|v|vi{1,3}|ix|x)$")]
    private static partial Regex RomanNumeral();

    private static string Slug(string s)
    {
        var slug = string.Join('-', ShotTiming.Tokens(s));
        return slug.Length > 40 ? slug[..40] : slug;
    }
}
