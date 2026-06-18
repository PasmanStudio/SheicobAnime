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
    /// Throws on HTTP error, safety block, or empty response.
    /// </summary>
    public async Task<string> GenerateAsync(
        string systemInstruction,
        string userPrompt,
        bool useWebSearch,
        CancellationToken ct = default)
    {
        var generationConfig = new Dictionary<string, object?>
        {
            ["temperature"] = settings.Temperature,
        };
        // Tools + structured-output MIME type are mutually exclusive in the Gemini API.
        if (!useWebSearch)
            generationConfig["responseMimeType"] = "application/json";

        var payload = new Dictionary<string, object?>
        {
            ["system_instruction"] = new { parts = new[] { new { text = systemInstruction } } },
            ["contents"]           = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
            ["generationConfig"]   = generationConfig,
        };
        if (useWebSearch)
            payload["tools"] = new[] { new { google_search = new { } } };

        var json = JsonSerializer.Serialize(payload);
        var url  = $"{BaseUrl}/{settings.Model}:generateContent?key={Uri.EscapeDataString(settings.ApiKey)}";
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
                    settings.Model, text.Length, useWebSearch, attempt);
                return text;
            }

            var transient = resp.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable;
            if (transient && attempt < maxAttempts)
            {
                logger.LogDebug("Gemini {Model} {Code} (attempt {Attempt}) — retrying",
                    settings.Model, (int)resp.StatusCode, attempt);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }

            throw new InvalidOperationException(
                $"Gemini {settings.Model} failed ({(int)resp.StatusCode}): {Truncate(body, 400)}");
        }
    }

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

        var result = sb.ToString().Trim();
        if (result.Length == 0)
            throw new InvalidOperationException("Gemini candidate had no text");
        return result;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
