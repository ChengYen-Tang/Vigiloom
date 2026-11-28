using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Agent;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-status", async (Guid deviceId, HttpContext context, IAgentStatusReader monitoring, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await monitoring.GetAsync(deviceId, token) is { } result ? Results.Ok(result) : Results.NotFound();
        })
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentConnectionStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Get Agent connectivity independently of device Ping");
        var admin = app.MapGroup("/api/v1/agent-management").RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, IAgentRegistration agents, CancellationToken token) =>
            await agents.GetDeviceAgentsAsync(deviceId, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .Produces<DeviceAgents>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("List a device's Agent registrations and enrollment status");
        admin.MapPost("/devices/{deviceId:guid}/enrollments", async (Guid deviceId, HttpContext context, IAgentRegistration agents, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await agents.IssueAsync(deviceId, actor, token);
                return result.Status switch
                {
                    AgentIdentityStatus.Success => Results.Ok(new EnrollmentSecretResponse(result.Secret!, result.ExpiresAt!.Value)),
                    AgentIdentityStatus.NotFound => Results.NotFound(),
                    _ => Results.Conflict()
                };
            }, allowApiCredential: false))
            .Produces<EnrollmentSecretResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Issue a one-time, device-bound Agent enrollment secret");
        admin.MapPost("/agents/{agentId:guid}/revoke", async (Guid agentId, HttpContext context, IAgentRegistration agents, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
                await agents.RevokeAsync(agentId, actor, token) == AgentIdentityStatus.Success ? Results.NoContent() : Results.NotFound(), allowApiCredential: false))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Revoke an Agent credential");
        admin.MapPost("/enrollments/{enrollmentId:guid}/revoke", async (Guid enrollmentId, HttpContext context, IAgentRegistration agents, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
                await agents.RevokeEnrollmentAsync(enrollmentId, actor, token) == AgentIdentityStatus.Success ? Results.NoContent() : Results.NotFound(), allowApiCredential: false))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Revoke an Agent enrollment secret");

        var machine = app.MapGroup("/api/v1/agent").RequireRateLimiting("api-client");
        machine.MapPost("/enroll", async (EnrollmentRequest input, HttpContext context, IMachineAgentIdentity agents, CancellationToken token) =>
        {
            if (!context.Request.IsHttps) return Results.BadRequest();
            var result = await agents.RedeemAsync(input.EnrollmentSecret, input.MachineVerifier, token);
            return result.Status == AgentIdentityStatus.Success ? Results.Ok(new EnrollmentResponse(result.AgentId!.Value)) : Results.Unauthorized();
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048))
            .Produces<EnrollmentResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(413).ProducesProblem(429)
            .WithSummary("Enroll an Agent using a device-bound enrollment secret");
        machine.MapPost("/recover", async (EnrollmentRecoveryRequest input, HttpContext context, IMachineAgentIdentity agents, CancellationToken token) =>
        {
            var result = await agents.RecoverAsync(input.EnrollmentSecret, Bearer(context), token);
            return result.Status == AgentIdentityStatus.Success ? Results.Ok(new EnrollmentResponse(result.AgentId!.Value)) : Results.Unauthorized();
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048))
            .Produces<EnrollmentResponse>().ProducesProblem(401).ProducesProblem(413).ProducesProblem(429)
            .WithSummary("Recover an existing Agent ID using enrollment and machine secret proof");
        machine.MapPost("/rotation/start", async (RotationRequest input, HttpContext context, IMachineAgentIdentity agents, CancellationToken token) =>
        {
            var result = await agents.BeginRotationAsync(Bearer(context), input.MachineVerifier, token);
            return result switch { AgentIdentityStatus.Success => Results.NoContent(), AgentIdentityStatus.Conflict => Results.Conflict(), _ => Results.Unauthorized() };
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024))
            .Produces(204).ProducesProblem(401).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429)
            .WithSummary("Begin a machine-initiated credential rotation using the current Agent credential");
        machine.MapPost("/rotation/finish", async (HttpContext context, IMachineAgentIdentity agents, CancellationToken token) =>
            await agents.FinishRotationAsync(Bearer(context), token) == AgentIdentityStatus.Success ? Results.NoContent() : Results.Unauthorized())
            .AllowAnonymous().Produces(204).ProducesProblem(401).ProducesProblem(429)
            .WithSummary("Confirm a pending rotation using the replacement Agent credential");
    }

    internal static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return context.Request.IsHttps && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
    }
}
