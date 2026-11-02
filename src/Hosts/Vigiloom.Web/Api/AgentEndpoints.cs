using System.Security.Claims;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Contracts.Devices;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-status", async (Guid deviceId, HttpContext context, AgentMonitoring monitoring, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await monitoring.GetAsync(deviceId, token) is { } result ? Results.Ok(result) : Results.NotFound();
        })
            .RequireAuthorization("DeviceRead").RequireRateLimiting("api-client")
            .Produces<AgentConnectionStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Get Agent connectivity independently of device Ping");
        var admin = app.MapGroup("/api/v1/agent-management").RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, AgentIdentity agents, CancellationToken token) =>
            await agents.GetDeviceAgentsAsync(deviceId, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .Produces<DeviceAgents>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("List a device's Agent registrations and enrollment status");
        admin.MapPost("/devices/{deviceId:guid}/enrollments", async (Guid deviceId, HttpContext context, AgentIdentity agents, CancellationToken token) =>
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
        admin.MapPost("/agents/{agentId:guid}/revoke", async (Guid agentId, HttpContext context, AgentIdentity agents, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
                await agents.RevokeAsync(agentId, actor, token) == AgentIdentityStatus.Success ? Results.NoContent() : Results.NotFound(), allowApiCredential: false))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Revoke an Agent credential");
        admin.MapPost("/enrollments/{enrollmentId:guid}/revoke", async (Guid enrollmentId, HttpContext context, AgentIdentity agents, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
                await agents.RevokeEnrollmentAsync(enrollmentId, actor, token) == AgentIdentityStatus.Success ? Results.NoContent() : Results.NotFound(), allowApiCredential: false))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Revoke an Agent enrollment secret");

        var machine = app.MapGroup("/api/v1/agent").RequireRateLimiting("api-client");
        machine.MapPost("/enroll", async (EnrollmentRequest input, HttpContext context, AgentIdentity agents, CancellationToken token) =>
        {
            if (!context.Request.IsHttps) return Results.BadRequest();
            var result = await agents.RedeemAsync(input.EnrollmentSecret, input.MachineVerifier, token);
            return result.Status == AgentIdentityStatus.Success ? Results.Ok(new EnrollmentResponse(result.AgentId!.Value)) : Results.Unauthorized();
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048))
            .Produces<EnrollmentResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(413).ProducesProblem(429)
            .WithSummary("Enroll an Agent using a device-bound enrollment secret");
        machine.MapPost("/recover", async (EnrollmentRecoveryRequest input, HttpContext context, AgentIdentity agents, CancellationToken token) =>
        {
            var result = await agents.RecoverAsync(input.EnrollmentSecret, Bearer(context), token);
            return result.Status == AgentIdentityStatus.Success ? Results.Ok(new EnrollmentResponse(result.AgentId!.Value)) : Results.Unauthorized();
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048))
            .Produces<EnrollmentResponse>().ProducesProblem(401).ProducesProblem(413).ProducesProblem(429)
            .WithSummary("Recover an existing Agent ID using enrollment and machine secret proof");
        machine.MapGet("/identity", (HttpContext context) => Results.Ok(new AgentIdentityResponse(
            Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Guid.Parse(context.User.FindFirstValue("vigiloom:device-id")!))))
            .RequireAuthorization("Agent").Produces<AgentIdentityResponse>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Inspect the current Agent identity");
        machine.MapPost("/heartbeat", async (HttpContext context, AgentIdentity agents, CancellationToken token) =>
            await agents.RecordHeartbeatAsync(Bearer(context), token) == AgentIdentityStatus.Success ? Results.NoContent() : Results.Unauthorized())
            .RequireAuthorization("Agent").RequireRateLimiting("agent-transport")
            .WithMetadata(AgentTransportEndpoint.Instance, new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(0))
            .Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(413).ProducesProblem(429)
            .WithSummary("Record an authenticated Agent heartbeat");
        machine.MapPost("/rotation/start", async (RotationRequest input, HttpContext context, AgentIdentity agents, CancellationToken token) =>
        {
            var result = await agents.BeginRotationAsync(Bearer(context), input.MachineVerifier, token);
            return result switch { AgentIdentityStatus.Success => Results.NoContent(), AgentIdentityStatus.Conflict => Results.Conflict(), _ => Results.Unauthorized() };
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024))
            .Produces(204).ProducesProblem(401).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429)
            .WithSummary("Begin a machine-initiated credential rotation using the current Agent credential");
        machine.MapPost("/rotation/finish", async (HttpContext context, AgentIdentity agents, CancellationToken token) =>
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

/// <summary>One-time Agent enrollment data. The verifier is the SHA-256 digest of a locally generated 256-bit secret.</summary>
public sealed record EnrollmentRequest(string? EnrollmentSecret, byte[]? MachineVerifier);
/// <summary>Enrollment secret associated with the missing registration response.</summary>
public sealed record EnrollmentRecoveryRequest(string? EnrollmentSecret);
/// <summary>SHA-256 verifier of a new 256-bit machine-generated secret.</summary>
public sealed record RotationRequest(byte[]? MachineVerifier);
/// <summary>Enrollment secret visible only at issuance.</summary>
public sealed record EnrollmentSecretResponse(string Secret, DateTimeOffset ExpiresAt);
/// <summary>The Agent identity created or recovered by enrollment.</summary>
public sealed record EnrollmentResponse(Guid AgentId);
/// <summary>The authenticated machine and its bound device.</summary>
public sealed record AgentIdentityResponse(Guid AgentId, Guid DeviceId);
