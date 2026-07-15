using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.AiRewrite;

/// <summary>
/// Thin wrapper over Google Gemini's generateContent endpoint (free tier).
/// Returns the model's raw text output; JSON parsing is the caller's job.
///
/// Two modes:
///   • Web search ON  → attaches the google_search tool for grounding. The API forbids
///                       responseMimeType=application/json alongside tools, so JSON is
///                       requested via the prompt and parsed tolerantly by the caller.
///   • Web search OFF → sets responseMimeType=application/json for a guaranteed JSON body.
///
/// Isolated on purpose: swapping providers later only touches this file.
/// </summary>
public class GeminiClient(
    IHttpClientFactory httpFactory,
    AiSettings settings,
    ILogger<GeminiClient> logger)
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    /// <summary>
    /// Sends one prompt and returns the concatenated text of the first candidate.
    /// Throws on HTTP error, safety block, or empty response. Si el modelo
    /// principal agota su cuota free tier (429 — pasa a diario: Google recortó
    /// el límite a ~20 req/día en jul-2026 y las corridas de la noche quedaban
    /// sin IA), reintenta UNA vez con <see cref="AiSettings.FallbackModel"/>
    /// (Gemma: cuota separada y enorme, sin grounding).
    /// </summary>
    // Fallback descubierto vía ListModels cuando el configurado devuelve 404
    // (Google renombra los Gemma entre generaciones: gemma-3-27b-it dio 404 en
    // jul-2026 y todas las corridas cayeron a heurística). Se resuelve una vez
    // por proceso.
    private string? _resolvedFallback;

    public async Task<string> GenerateAsync(
        string systemInstruction,
        string userPrompt,
        bool useWebSearch,
        CancellationToken ct = default)
    {
        try
        {
            return await GenerateWithModelAsync(settings.Model, systemInstruction, userPrompt, useWebSearch, ct);
        }
        catch (GeminiQuotaException) when (
            !string.IsNullOrWhiteSpace(settings.FallbackModel)
            && !string.Equals(settings.FallbackModel, settings.Model, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = _resolvedFallback ?? settings.FallbackModel;
            logger.LogWarning("Gemini {Model} sin cuota (429) — fallback a {Fallback}",
                settings.Model, fallback);
            try
            {
                return await GenerateWithModelAsync(fallback, systemInstruction, userPrompt, useWebSearch, ct);
            }
            catch (GeminiModelNotFoundException) when (_resolvedFallback is null)
            {
                var discovered = await DiscoverFallbackModelAsync(ct);
                if (discovered is null
                    || string.Equals(discovered, fallback, StringComparison.OrdinalIgnoreCase))
                    throw;

                _resolvedFallback = discovered;
                logger.LogWarning(
                    "Fallback {Fallback} no existe (404) — usando {Discovered} descubierto vía ListModels",
                    fallback, discovered);
                return await GenerateWithModelAsync(discovered, systemInstruction, userPrompt, useWebSearch, ct);
            }
        }
    }

    /// <summary>
    /// Pregunta a la API qué modelos Gemma existen HOY para esta key (GET
    /// ListModels) y elige el más grande instruction-tuned. Los Gemma son la
    /// familia con cuota free tier separada — el nombre exacto cambia entre
    /// generaciones, así que descubrirlo es más robusto que hardcodearlo.
    /// </summary>
    private async Task<string?> DiscoverFallbackModelAsync(CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("gemini");
            var body = await http.GetStringAsync(
                $"{BaseUrl}?key={Uri.EscapeDataString(settings.ApiKey)}&pageSize=1000", ct);
            return PickFallbackFromModelList(body);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ListModels para descubrir el modelo de fallback falló");
            return null;
        }
    }

    /// <summary>
    /// Del JSON de ListModels, elige el mejor Gemma que soporte generateContent:
    /// instruction-tuned (-it) primero, después el de más parámetros (…27b/31b).
    /// Público estático para tests.
    /// </summary>
    public static string? PickFallbackFromModelList(string modelsJson)
    {
        using var doc = JsonDocument.Parse(modelsJson);
        if (!doc.RootElement.TryGetProperty("models", out var models)) return null;

        string? best = null;
        var bestRank = -1;
        foreach (var model in models.EnumerateArray())
        {
            var name = model.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.Contains("gemma", StringComparison.OrdinalIgnoreCase)) continue;
            if (!model.TryGetProperty("supportedGenerationMethods", out var methods)
                || !methods.EnumerateArray().Any(m => m.GetString() == "generateContent"))
                continue;

            var id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;
            var size = System.Text.RegularExpressions.Regex.Match(id, @"(\d+)b");
            var rank = size.Success ? int.Parse(size.Groups[1].Value) : 0;
            if (id.EndsWith("-it", StringComparison.OrdinalIgnoreCase)) rank += 1000;

            if (rank > bestRank)
            {
                bestRank = rank;
                best = id;
            }
        }
        return best;
    }

    private async Task<string> GenerateWithModelAsync(
        string model,
        string systemInstruction,
        string userPrompt,
        bool useWebSearch,
        CancellationToken ct)
    {
        // Los Gemma no soportan system_instruction, tools (grounding) ni JSON
        // mode nativo — se adapta el payload: instrucción fusionada al prompt
        // y JSON pedido por prompt (los callers ya lo piden y parsean tolerante).
        var isGemma = model.StartsWith("gemma", StringComparison.OrdinalIgnoreCase);

        var generationConfig = new Dictionary<string, object?>
        {
            ["temperature"] = settings.Temperature,
        };
        // Tools + structured-output MIME type are mutually exclusive in the Gemini API.
        if (!useWebSearch && !isGemma)
            generationConfig["responseMimeType"] = "application/json";

        var userText = isGemma ? $"{systemInstruction}\n\n---\n\n{userPrompt}" : userPrompt;
        var payload = new Dictionary<string, object?>
        {
            ["contents"]         = new[] { new { role = "user", parts = new[] { new { text = userText } } } },
            ["generationConfig"] = generationConfig,
        };
        if (!isGemma)
            payload["system_instruction"] = new { parts = new[] { new { text = systemInstruction } } };
        if (useWebSearch && !isGemma)
            payload["tools"] = new[] { new { google_search = new { } } };

        var json = JsonSerializer.Serialize(payload);
        var url  = $"{BaseUrl}/{model}:generateContent?key={Uri.EscapeDataString(settings.ApiKey)}";
        var http = httpFactory.CreateClient("gemini");

        // Retry only on 503 ("high demand") — that's transient. NOT on 429: that's a daily/quota
        // limit that won't clear in seconds, and retrying would just burn more of the free quota.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                var text = ExtractText(body);
                logger.LogDebug("Gemini {Model} ok ({Chars} chars, websearch={Ws}, attempt={Attempt})",
                    model, text.Length, useWebSearch, attempt);
                return text;
            }

            var transient = resp.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable;
            if (transient && attempt < maxAttempts)
            {
                logger.LogDebug("Gemini {Model} {Code} (attempt {Attempt}) — retrying",
                    model, (int)resp.StatusCode, attempt);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }

            var message = $"Gemini {model} failed ({(int)resp.StatusCode}): {Truncate(body, 400)}";
            throw resp.StatusCode switch
            {
                System.Net.HttpStatusCode.TooManyRequests => new GeminiQuotaException(message),
                System.Net.HttpStatusCode.NotFound        => new GeminiModelNotFoundException(message),
                _                                         => new InvalidOperationException(message),
            };
        }
    }

    /// <summary>429 del free tier — dispara el fallback de modelo.</summary>
    private sealed class GeminiQuotaException(string message) : InvalidOperationException(message);

    /// <summary>404: el modelo no existe (Google lo renombró/retiró) — dispara el descubrimiento.</summary>
    private sealed class GeminiModelNotFoundException(string message) : InvalidOperationException(message);

    /// <summary>Pulls candidates[0].content.parts[*].text out of the Gemini response envelope.</summary>
    private static string ExtractText(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("promptFeedback", out var fb)
            && fb.TryGetProperty("blockReason", out var reason))
            throw new InvalidOperationException($"Gemini blocked the prompt: {reason.GetString()}");

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini returned no candidates");

        var sb = new StringBuilder();
        if (candidates[0].TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var text))
                    sb.Append(text.GetString());
        }

        var result = StripCodeFences(sb.ToString());
        if (result.Length == 0)
            throw new InvalidOperationException("Gemini candidate had no text");
        return result;
    }

    /// <summary>
    /// Quita el fence markdown (```json … ```) que envuelve la respuesta cuando
    /// el modelo no soporta JSON mode nativo (Gemma, o Gemini con tools). Los
    /// callers hacen JsonDocument.Parse directo — sin esto, el fence rompe el
    /// parseo y la corrida cae a heurística. Público estático para tests.
    /// </summary>
    public static string StripCodeFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;

        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0) return t;
        t = t[(firstNewline + 1)..];

        var closing = t.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0) t = t[..closing];
        return t.Trim();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
