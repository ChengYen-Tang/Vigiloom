using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Application.AgentIdentity;

namespace Vigiloom.Resources.AgentPackages;

internal static class AgentRegistrationEndpoints
{
    internal static void MapInternalAgentRegistration(this RouteGroupBuilder api)
    {
        api.MapPost("/devices/agent-overview", (Guid[] deviceIds, AgentRegistration agents, CancellationToken token) => agents.OverviewAsync(deviceIds, token));
        api.MapPost("/agents/active", (Guid[] agentIds, AgentRegistration agents, CancellationToken token) => agents.ActiveAsync(agentIds, token));
        api.MapGet("/devices/{id:guid}/agents", async (Guid id, AgentRegistration agents, CancellationToken token) =>
            await agents.ListAsync(id, token) is { } registrations ? Results.Ok(registrations) : Results.NotFound());
        api.MapPost("/devices/{id:guid}/agent-enrollments", async (Guid id, AgentEnrollmentCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.IssueAsync(id, request.ActorId, token)));
        api.MapPost("/agent-enrollments/redeem", async (AgentRedeemCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.RedeemAsync(request.Secret, request.MachineVerifier, token)));
        api.MapPost("/agent-enrollments/recover", async (AgentRecoveryCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.RecoverAsync(request.EnrollmentSecret, request.MachineProof, token)));
        api.MapPost("/agents/authenticate", async (AgentCredentialCommand request, AgentRegistration agents, CancellationToken token) =>
            await agents.AuthenticateAsync(request.Credential, token) is { } identity ? Results.Ok(identity) : Results.Unauthorized());
        api.MapPost("/agents/{id:guid}/revoke", async (Guid id, AgentRevokeCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.RevokeAsync(id, request.ActorId, token)));
        api.MapPost("/agent-enrollments/{id:guid}/revoke", async (Guid id, AgentRevokeCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.RevokeEnrollmentAsync(id, request.ActorId, token)));
        api.MapPost("/agents/rotation/start", async (AgentRotationCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.BeginRotationAsync(request.Credential, request.MachineVerifier, token)));
        api.MapPost("/agents/rotation/finish", async (AgentCredentialCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.FinishRotationAsync(request.Credential, token)));
    }
}
