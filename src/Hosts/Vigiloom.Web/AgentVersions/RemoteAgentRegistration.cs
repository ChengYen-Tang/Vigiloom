using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.AgentVersions;

internal sealed class RemoteAgentRegistration(HttpClient client) : IAgentRegistration, IMachineAgentIdentity
{
    public async Task<DeviceAgents?> GetDeviceAgentsAsync(Guid deviceId, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/devices/{deviceId:D}/agents", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<DeviceAgentRegistrations>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned empty Agent registrations.");
        return new(data.Agents.Select(a => new AgentSummary(a.Id, a.DeviceGeneration, a.CreatedAt, a.RevokedAt)).ToArray(),
            data.Enrollments.Select(e => new EnrollmentSummary(e.Id, e.DeviceGeneration, e.CreatedAt, e.ExpiresAt, e.RevokedAt, e.Consumed)).ToArray(),
            data.CurrentGeneration);
    }

    public async Task<EnrollmentIssue> IssueAsync(Guid deviceId, Guid actorId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync($"internal/v1/devices/{deviceId:D}/agent-enrollments", new AgentEnrollmentCommand(actorId), token);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<AgentEnrollmentIssue>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned an empty enrollment result.");
        return new(ConvertStatus(data.Status), data.Secret, data.ExpiresAt);
    }

    public Task<AgentIdentityStatus> RevokeAsync(Guid agentId, Guid actorId, CancellationToken token) =>
        RevokeAsync($"internal/v1/agents/{agentId:D}/revoke", actorId, token);

    public Task<AgentIdentityStatus> RevokeEnrollmentAsync(Guid enrollmentId, Guid actorId, CancellationToken token) =>
        RevokeAsync($"internal/v1/agent-enrollments/{enrollmentId:D}/revoke", actorId, token);

    public async Task<EnrollmentResult> RedeemAsync(string? enrollmentSecret, byte[]? machineVerifier, CancellationToken token)
    {
        var result = await PostAsync<AgentRedeemCommand, AgentEnrollmentResult>("internal/v1/agent-enrollments/redeem", new(enrollmentSecret, machineVerifier), token);
        return new(ConvertStatus(result.Status), result.AgentId);
    }

    public async Task<EnrollmentResult> RecoverAsync(string? enrollmentSecret, string? machineProof, CancellationToken token)
    {
        var result = await PostAsync<AgentRecoveryCommand, AgentEnrollmentResult>("internal/v1/agent-enrollments/recover", new(enrollmentSecret, machineProof), token);
        return new(ConvertStatus(result.Status), result.AgentId);
    }

    public async Task<AgentPrincipal?> AuthenticateAsync(string? credential, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/agents/authenticate", new AgentCredentialCommand(credential), token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RegisteredAgentIdentity>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned an empty Agent identity.");
        return new(result.AgentId, result.DeviceId, result.DeviceGeneration);
    }

    public async Task<AgentIdentityStatus> BeginRotationAsync(string? credential, byte[]? newVerifier, CancellationToken token) =>
        ConvertStatus(await PostAsync<AgentRotationCommand, AgentRegistrationStatus>("internal/v1/agents/rotation/start", new(credential, newVerifier), token));

    public async Task<AgentIdentityStatus> FinishRotationAsync(string? credential, CancellationToken token) =>
        ConvertStatus(await PostAsync<AgentCredentialCommand, AgentRegistrationStatus>("internal/v1/agents/rotation/finish", new(credential), token));

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned an empty response.");
    }

    private async Task<AgentIdentityStatus> RevokeAsync(string path, Guid actorId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, new AgentRevokeCommand(actorId), token);
        response.EnsureSuccessStatusCode();
        return ConvertStatus(await response.Content.ReadFromJsonAsync<AgentRegistrationStatus>(cancellationToken: token));
    }

    private static AgentIdentityStatus ConvertStatus(AgentRegistrationStatus status) => status switch
    {
        AgentRegistrationStatus.Success => AgentIdentityStatus.Success,
        AgentRegistrationStatus.Conflict => AgentIdentityStatus.Conflict,
        AgentRegistrationStatus.NotFound => AgentIdentityStatus.NotFound,
        _ => AgentIdentityStatus.Invalid
    };
}
