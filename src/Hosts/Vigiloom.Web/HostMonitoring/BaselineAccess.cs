using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Application.Baseline;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class BaselineAccess(BaselineQuery query, BaselineExceptions exceptions, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);
    public async Task<DeviceBaselineView?> GetAsync(Guid deviceId, CancellationToken token)
    {
        if (!Remote) return await query.GetAsync(deviceId, token);
        using var response = await clients.CreateClient("security").GetAsync($"internal/v1/devices/{deviceId:D}/baseline", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty baseline.");
    }

    public async Task<BaselineExceptionStatus> RecordAsync(Guid deviceId, string sourceKey, Guid revisionId, string? reason, Guid actor, CancellationToken token)
    {
        if (!Remote) return await exceptions.RecordAsync(deviceId, sourceKey, revisionId, reason, actor, token);
        using var response = await clients.CreateClient("security").PostAsJsonAsync($"internal/v1/devices/{deviceId:D}/baseline-exceptions",
            new { Input = new BaselineExceptionInput(sourceKey, revisionId, reason ?? ""), ActorId = actor }, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BaselineExceptionStatus>(cancellationToken: token);
    }
}
