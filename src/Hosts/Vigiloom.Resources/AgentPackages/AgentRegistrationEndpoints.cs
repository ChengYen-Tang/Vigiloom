using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Application.AgentIdentity;

namespace Vigiloom.Resources.AgentPackages;

internal static class AgentRegistrationEndpoints
{
    internal static void MapInternalAgentRegistration(this RouteGroupBuilder api)
    {
        api.MapPost("/devices/{id:guid}/agent-enrollments", async (Guid id, AgentEnrollmentCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.IssueAsync(id, request.ActorId, token)));
        api.MapPost("/agent-enrollments/redeem", async (AgentRedeemCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.RedeemAsync(request.Secret, request.MachineVerifier, token)));
        api.MapPost("/agents/authenticate", async (AgentCredentialCommand request, AgentRegistration agents, CancellationToken token) =>
            await agents.AuthenticateAsync(request.Credential, token) is { } identity ? Results.Ok(identity) : Results.Unauthorized());
        api.MapPost("/agents/{id:guid}/revoke", async (Guid id, AgentRevokeCommand request, AgentRegistration agents, CancellationToken token) =>
            Results.Ok(await agents.RevokeAsync(id, request.ActorId, token)));
    }
}
