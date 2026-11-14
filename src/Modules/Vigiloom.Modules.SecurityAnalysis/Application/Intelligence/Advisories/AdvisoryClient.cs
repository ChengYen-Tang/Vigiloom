using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

internal sealed record AdvisoryPage(IReadOnlyList<AdvisoryRecord> Records, string? NextCursor, byte[] RawBytes);

internal sealed class AdvisoryRateLimitException(DateTimeOffset? retryAt) : Exception("GitHub advisory rate limit reached.")
{
    internal DateTimeOffset? RetryAt { get; } = retryAt;
}

/// <summary>Fetches one bounded GitHub-reviewed advisory page within a fixed modified-date window.</summary>
internal sealed class AdvisoryClient(HttpClient client, TimeProvider clock)
{
    internal const int MaximumPageBytes = 2 * 1024 * 1024;

    internal async Task<AdvisoryPage> FetchAsync(DateOnly from, DateOnly to, bool withdrawn, string? cursor, string? token, CancellationToken cancellationToken)
    {
        if (to < from || cursor is not null && !AdvisoryLinks.ValidCursor(cursor)) throw new ArgumentException("Invalid advisory window or cursor.");
        var modified = $"{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}..{to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        var query = $"https://api.github.com/advisories?type=reviewed&per_page=100&sort=updated&direction=asc&modified={modified}";
        if (withdrawn) query += "&is_withdrawn=true";
        if (cursor is not null) query += $"&after={Uri.EscapeDataString(cursor)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.UserAgent.ParseAdd("Vigiloom/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.StatusCode == HttpStatusCode.Forbidden && (response.Headers.RetryAfter is not null ||
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.SingleOrDefault() == "0" ||
                await IsSecondaryLimitAsync(response.Content, timeout.Token)))
            throw new AdvisoryRateLimitException(RetryAt(response) ?? clock.GetUtcNow().AddMinutes(1));
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Unexpected advisory response status.");
        var bytes = await BoundedHttpBody.ReadAsync(response.Content, MaximumPageBytes, timeout.Token);
        var records = AdvisoryPageValidator.Validate(bytes);
        if (withdrawn && records.Any(x => x.WithdrawnAt is null)) throw new InvalidDataException("Withdrawn advisory query returned an active record.");
        var next = response.Headers.TryGetValues("Link", out var links) ? AdvisoryLinks.Next(links, modified, withdrawn) : null;
        if (next == cursor && next is not null) throw new InvalidDataException("Advisory cursor did not advance.");
        return new(records, next, bytes);
    }

    private static async Task<bool> IsSecondaryLimitAsync(HttpContent content, CancellationToken token)
    {
        var bytes = await BoundedHttpBody.ReadAsync(content, 64 * 1024, token);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                message.GetString()!.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private DateTimeOffset? RetryAt(HttpResponseMessage response)
    {
        var now = clock.GetUtcNow();
        if (response.Headers.RetryAfter?.Delta is { } delta) return now.Add(delta);
        if (response.Headers.RetryAfter?.Date is { } date) return date.ToUniversalTime();
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.SingleOrDefault() == "0" &&
            response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.SingleOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return null;
    }
}
