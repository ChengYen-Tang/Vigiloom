using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteHostLogs(HttpClient client, IDeviceManagement devices) : IHostLogHistory
{
    public async Task<HostLogPage?> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, Guid? sourceId, CancellationToken token)
    {
        if (deviceId == Guid.Empty || until <= from || until - from > TimeSpan.FromDays(31) || sourceId is { Version: not 7 })
            throw new ArgumentException("Invalid host log query.");
        if (await devices.GetAsync(deviceId, token) is null) return null;
        var path = QueryHelpers.AddQueryString($"internal/v1/host-logs/{deviceId:D}", new Dictionary<string, string?>
        {
            ["from"] = from.ToUniversalTime().ToString("O"), ["until"] = until.ToUniversalTime().ToString("O"),
            ["cursor"] = cursor, ["sourceId"] = sourceId?.ToString("D")
        });
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid host log query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostLogPage>(cancellationToken: token)
            ?? throw new InvalidOperationException("Operations returned an empty host log page.");
    }
}
