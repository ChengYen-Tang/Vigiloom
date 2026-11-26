using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Modules.AlertingOperations.Application;

namespace Vigiloom.Web.Operations;

internal sealed class OperationsNotificationReader(NotificationReader local, IConfiguration configuration, IServiceProvider services) : INotificationReader
{
    public Task<NotificationPage> GetRecentAsync(NotificationQuery query, CancellationToken token) =>
        ReadAsync("notifications", query, local.GetRecentAsync, token);

    public Task<AlertEpisodePage> GetEpisodesAsync(NotificationQuery query, CancellationToken token) =>
        ReadAsync("alerts", query, local.GetEpisodesAsync, token);

    private Task<T> ReadAsync<T>(string category, NotificationQuery query, Func<NotificationQuery, CancellationToken, Task<T>> fallback, CancellationToken token) where T : notnull
    {
        if (query.Page is < 1 or > 1000 || query.Source is { Length: > 64 } || query.Start > query.End)
            throw new ArgumentOutOfRangeException(nameof(query));
        return string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"])
            ? fallback(query, token) : services.GetRequiredService<RemoteNotificationReader>().ReadAsync<T>(category, query, token);
    }
}

internal sealed class RemoteNotificationReader(HttpClient client)
{
    public async Task<T> ReadAsync<T>(string category, NotificationQuery query, CancellationToken token) where T : notnull
    {
        var path = QueryHelpers.AddQueryString($"internal/v1/history/{category}", new Dictionary<string, string?>
        {
            ["page"] = query.Page.ToString(CultureInfo.InvariantCulture), ["source"] = query.Source,
            ["start"] = query.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["end"] = query.End?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });
        return await client.GetFromJsonAsync<T>(path, token)
            ?? throw new InvalidOperationException("Operations service returned an empty notification page.");
    }
}
