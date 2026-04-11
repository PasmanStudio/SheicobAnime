using AnimeIndex.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Marks a ScrapeJob as dead_letter and sends an email alert via the Resend API
/// when AttemptCount reaches the threshold (default: 3).
/// </summary>
public class DeadLetterAlerter(
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<DeadLetterAlerter> logger)
{
    private const int DeadLetterThreshold = 3;

    private readonly HttpClient _http = httpClientFactory.CreateClient("resend");

    /// <summary>
    /// Call after every failed scrape attempt.
    /// Increments AttemptCount. If threshold reached, marks as dead_letter and emails.
    /// </summary>
    public async Task HandleFailureAsync(
        Guid scrapeJobId, string errorMessage, CancellationToken ct = default)
    {
        var job = await db.ScrapeJobs.FindAsync([scrapeJobId], ct);
        if (job is null) return;

        job.AttemptCount++;
        job.ErrorMessage = errorMessage[..Math.Min(errorMessage.Length, 2000)];

        if (job.AttemptCount >= DeadLetterThreshold)
        {
            job.Status = "dead_letter";
            job.CompletedAt = DateTime.UtcNow;
            logger.LogError(
                "ScrapeJob {JobId} reached dead_letter after {Attempts} attempts. Error: {Error}",
                scrapeJobId, job.AttemptCount, errorMessage);
            await db.SaveChangesAsync(ct);
            await SendAlertAsync(job.Id, job.JobType, job.AttemptCount, errorMessage, ct);
        }
        else
        {
            job.Status = "failed";
            logger.LogWarning(
                "ScrapeJob {JobId} failed (attempt {Attempt}/{Threshold}): {Error}",
                scrapeJobId, job.AttemptCount, DeadLetterThreshold, errorMessage);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Mark a job as successfully completed.</summary>
    public async Task HandleSuccessAsync(Guid scrapeJobId, CancellationToken ct = default)
    {
        var job = await db.ScrapeJobs.FindAsync([scrapeJobId], ct);
        if (job is null) return;

        job.Status = "completed";
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task SendAlertAsync(
        Guid jobId, string jobType, short attempts, string error, CancellationToken ct)
    {
        var apiKey = config["Resend:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("RESEND_API_KEY not configured — dead-letter alert not sent");
            return;
        }

        var fromEmail = config["Resend:FromEmail"] ?? "alerts@sheicobanime.com";
        var toEmail = config["Resend:ToEmail"] ?? config["RESEND_TO_EMAIL"];
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            logger.LogWarning("Resend:ToEmail not configured — dead-letter alert not sent");
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            from = fromEmail,
            to = new[] { toEmail },
            subject = $"[SheicobAnime] Dead-letter: {jobType} ({jobId})",
            html = $"""
                <h2>Scrape Job Dead-lettered</h2>
                <p><strong>Job ID:</strong> {jobId}</p>
                <p><strong>Job Type:</strong> {jobType}</p>
                <p><strong>Attempts:</strong> {attempts}</p>
                <p><strong>Last Error:</strong></p>
                <pre>{HtmlEncode(error)}</pre>
                """
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("Resend API error {Status}: {Body}", response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send dead-letter alert for job {JobId}", jobId);
        }
    }

    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
