using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Resources;

namespace Vigiloom.Security;

internal sealed class BaselineRemoteSources(IHttpClientFactory clients) : IAgentBaselineSyncReader, IBaselineExceptionReader
{
    public async Task<AgentBaselineSyncObservation?> GetAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient("host").GetAsync($"internal/v1/devices/{deviceId:D}/agent-security-observation", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var observation = await response.Content.ReadFromJsonAsync<SecurityObservation>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Host returned an empty security observation.");
        if (observation.Device is not { Eligible: true } || observation.Agent is not { } agent) return null;
        BaselineSettingsReport? report = null;
        if (agent.BaselineConfigurationJson is { } json)
        {
            try { report = JsonSerializer.Deserialize<BaselineSettingsReport>(json); }
            catch (JsonException) { }
            if (report is not null && !report.IsValid()) report = null;
        }
        return new(agent.Id, agent.LastHeartbeatAt, Math.Max(90, agent.HeartbeatSafetyIntervalSeconds * 3),
            report is null ? null : agent.BaselineConfigurationReportedAt, report);
    }

    public async Task<IReadOnlyList<BaselineExceptionNote>> GetRecentAsync(Guid deviceId, CancellationToken cancellationToken) =>
        await clients.CreateClient("operations").GetFromJsonAsync<BaselineExceptionNote[]>(
            $"internal/v1/history/devices/{deviceId:D}/baseline-exceptions", cancellationToken)
        ?? throw new InvalidOperationException("Operations returned empty baseline notes.");

    private sealed record SecurityObservation(DeviceAgentState Device, AgentObservation? Agent);
    private sealed record AgentObservation(Guid Id, DateTimeOffset? LastHeartbeatAt, int HeartbeatSafetyIntervalSeconds,
        DateTimeOffset? BaselineConfigurationReportedAt, string? BaselineConfigurationJson);
}
