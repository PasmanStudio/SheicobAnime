namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>Una toma que se cambió porque ya se había presentado lo que muestra.</summary>
public sealed record IntroductionChange(int Scene, int Slot, string At, string Original, string? Now, string Reason);

/// <summary>Tomas del plan después del registro de presentaciones.</summary>
/// <param name="Specs">Por escena y por toma: qué se muestra, o null = metraje de la fuente de la escena.</param>
public sealed record IntroductionResult(IReadOnlyList<IReadOnlyList<ShotReference?>> Specs, IReadOnlyList<IntroductionChange> Changes);

/// <summary>
/// Cada personaje, póster, logo, persona y tarjeta se muestra UNA vez: cuando se
/// lo presenta ("…Weak Kingsley y Avida Yang" → ahí y nunca más). Cuando la voz lo
/// vuelve a nombrar, esa toma pasa a metraje de la fuente de la escena (lo que se
/// está contando), no a la misma imagen otra vez.
///   - Logos, personajes y personas con "+": se muestran solo los que faltan;
///     si ya estaban todos, metraje.
///   - Tarjetas: la misma tarjeta (texto e imagen) no se repite. Si su imagen de
///     acompañamiento (un póster, un personaje) ya se mostró, la tarjeta va sola
///     sobre el arte; si no, esa imagen queda presentada con la tarjeta.
///   - Escenas ("scene: …") e imágenes IA no se registran: no se repiten porque
///     el plano que eligió CLIP queda usado (<see cref="FootageLibrary"/>).
/// Se aplica al renderizar, sin tocar plan.json: --plan da siempre lo mismo.
/// </summary>
public static class IntroductionLedger
{
    /// <summary>Público para tests.</summary>
    public static IntroductionResult Apply(VideoPlan plan)
    {
        var shown = new Dictionary<string, int>(StringComparer.Ordinal); // clave → escena (1..n) donde se presentó
        var specs = new List<IReadOnlyList<ShotReference?>>();
        var changes = new List<IntroductionChange>();
        for (var i = 0; i < plan.Scenes.Count; i++)
        {
            var list = new List<ShotReference?>();
            var shots = plan.Scenes[i].Shots ?? [];
            for (var k = 0; k < shots.Count; k++)
            {
                var original = ShotReference.Parse(shots[k].Show);
                var (now, reason) = Resolve(original, shown, i + 1);
                list.Add(now);
                if (reason is not null)
                    changes.Add(new IntroductionChange(i + 1, k, shots[k].At, shots[k].Show, now is null ? null : Describe(now), reason));
            }
            specs.Add(list);
        }
        return new IntroductionResult(specs, changes);
    }

    private static (ShotReference? Now, string? Reason) Resolve(ShotReference r, Dictionary<string, int> shown, int scene)
    {
        switch (r.Kind)
        {
            case ShotKind.Poster or ShotKind.KeyArt:
            {
                var key = Key(r.Kind, r.Value);
                if (shown.TryGetValue(key, out var at)) return (null, $"ya presentado en la escena {at}");
                shown[key] = scene;
                return (r, null);
            }
            case ShotKind.Logo or ShotKind.Character or ShotKind.Photo:
            {
                var names = r.Names.Count > 0 ? r.Names : [r.Value];
                var fresh = names.Where(n => !shown.ContainsKey(Key(r.Kind, n))).ToList();
                if (fresh.Count == 0)
                    return (null, $"ya presentado en la escena {shown[Key(r.Kind, names[0])]}");
                foreach (var n in fresh) shown[Key(r.Kind, n)] = scene;
                if (fresh.Count == names.Count) return (r, null);
                var repeated = names.Except(fresh).ToList();
                return (r with { Value = string.Join(" + ", fresh) },
                    $"{string.Join(", ", repeated)} ya presentado (escena {shown[Key(r.Kind, repeated[0])]})");
            }
            case ShotKind.Card:
            {
                // "10 episodios" de la T1 y "10 episodios" de la T2 son datos distintos:
                // la tarjeta se identifica por su texto Y su imagen.
                var key = Key(r.Kind, $"{r.Value} {r.Detail} {r.Image}");
                if (shown.TryGetValue(key, out var at)) return (null, $"tarjeta ya mostrada en la escena {at}");
                shown[key] = scene;
                if (r.Image is null)
                {
                    var plain = Key(r.Kind, $"{r.Value} {r.Detail}");
                    if (shown.TryGetValue(plain, out var plainAt) && plainAt != scene)
                        return (null, $"tarjeta igual a la de la escena {plainAt}");
                    shown[plain] = scene;
                    return (r, null);
                }
                var image = ShotReference.Parse(r.Image);
                var imageKey = Key(image.Kind, image.Kind is ShotKind.Logo or ShotKind.Character or ShotKind.Photo
                    ? image.Names.FirstOrDefault() ?? image.Value : image.Value);
                if (shown.TryGetValue(imageKey, out var imageAt))
                {
                    // Sin su imagen puede quedar igual a una tarjeta ya mostrada
                    // ("10 episodios" de la T1 y de la T2): entonces tampoco va.
                    var bare = Key(r.Kind, $"{r.Value} {r.Detail}");
                    if (shown.TryGetValue(bare, out var bareAt)) return (null, $"tarjeta igual a la de la escena {bareAt}");
                    shown[bare] = scene;
                    return (r with { Image = null }, $"sin {r.Image}: ya presentado en la escena {imageAt}");
                }
                shown[imageKey] = scene;
                return (r, null);
            }
            default:
                return (r, null);
        }
    }

    private static string Key(ShotKind kind, string value)
    {
        // Póster y arte son "la serie"; personaje y foto son "la persona".
        var group = kind switch
        {
            ShotKind.Character or ShotKind.Photo => "person",
            _ => kind.ToString().ToLowerInvariant(),
        };
        return $"{group}:{string.Join(' ', ShotTiming.Tokens(value))}";
    }

    /// <summary>ShotReference → el "show" del plan (para el reporte).</summary>
    public static string Describe(ShotReference r) => r.Kind switch
    {
        ShotKind.KeyArt => "keyart",
        ShotKind.Card => "card: " + string.Join(" | ", new[] { r.Value, r.Detail, r.Image }.Where(p => !string.IsNullOrEmpty(p))),
        _ => $"{r.Kind.ToString().ToLowerInvariant()}: {r.Value}",
    };
}
