using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteInboxSettings(HttpClient client) : IAgentInboxSettings, IAgentReplayPolicy
{
    public async Task<AgentInboxSettingsSnapshot> GetAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<AgentInboxSettingsSnapshot>("internal/v1/agent-inbox", token) ?? throw new InvalidOperationException("Missing Agent inbox settings.");

    public Task<bool> ActivateCleanupAsync(string version, Guid actor, CancellationToken token) =>
        ChangeAsync("internal/v1/agent-inbox/cutover", new InboxCutoverCommand(version, actor), token);

    public async Task<AgentInboxSettingsPreview?> PreviewAsync(int replaySeconds, int retentionDays, string version, Guid actor, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/agent-inbox/preview", new InboxPreviewCommand(new(replaySeconds, retentionDays, version), actor), token);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid inbox preview.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentInboxSettingsPreview>(cancellationToken: token) ?? throw new InvalidOperationException("Missing Agent inbox preview.");
    }

    public Task<bool> UpdateAsync(int replaySeconds, int retentionDays, string version, string? confirmationToken, Guid actor, CancellationToken token) =>
        ChangeAsync("internal/v1/agent-inbox/update", new InboxUpdateCommand(new(replaySeconds, retentionDays, version, confirmationToken), actor), token);

    async Task<AgentReplayPolicyView> IAgentReplayPolicy.GetAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<AgentReplayPolicyView>("internal/v1/agent-replay-policy", token) ?? throw new InvalidOperationException("Missing Agent replay policy.");

    public async Task<AgentReplayPolicyView?> TransitionAsync(AgentReplayPhase target, string version, Guid actor, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/agent-replay-policy", new ReplayPolicyCommand(new(target, version, true), actor), token);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid replay policy transition.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentReplayPolicyView>(cancellationToken: token) ?? throw new InvalidOperationException("Missing Agent replay policy.");
    }

    private async Task<bool> ChangeAsync<T>(string path, T command, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, command, token);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid Agent inbox change.");
        response.EnsureSuccessStatusCode();
        return true;
    }
}
