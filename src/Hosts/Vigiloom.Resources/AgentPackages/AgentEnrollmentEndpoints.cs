using Microsoft.AspNetCore.Mvc;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Application.AgentIdentity;

namespace Vigiloom.Resources.AgentPackages;

internal static class AgentEnrollmentEndpoints
{
    internal static void MapAgentEnrollment(this WebApplication app)
    {
        var agent = app.MapGroup("/api/v1/agent").RequireRateLimiting("api-client");
        agent.MapPost("/enroll", async (EnrollmentRequest request, HttpContext context, AgentRegistration registrations, CancellationToken token) =>
        {
            if (!context.Request.IsHttps) return Results.BadRequest();
            var result = await registrations.RedeemAsync(request.EnrollmentSecret, request.MachineVerifier, token);
            return result.Status == AgentRegistrationStatus.Success ? Results.Ok(new EnrollmentResponse(result.AgentId!.Value)) : Results.Unauthorized();
        }).AllowAnonymous().WithMetadata(new RequestSizeLimitAttribute(2048));
        agent.MapPost("/recover", async (EnrollmentRecoveryRequest request, HttpContext context, AgentRegistration registrations, CancellationToken token) =>
        {
            var result = await registrations.RecoverAsync(request.EnrollmentSecret, Bearer(context), token);
            return result.Status == AgentRegistrationStatus.Success ? Results.Ok(new EnrollmentResponse(result.AgentId!.Value)) : Results.Unauthorized();
        }).AllowAnonymous().WithMetadata(new RequestSizeLimitAttribute(2048));
        agent.MapPost("/rotation/start", async (RotationRequest request, HttpContext context, AgentRegistration registrations, CancellationToken token) =>
            (await registrations.BeginRotationAsync(Bearer(context), request.MachineVerifier, token)) switch
            {
                AgentRegistrationStatus.Success => Results.NoContent(),
                AgentRegistrationStatus.Conflict => Results.Conflict(),
                _ => Results.Unauthorized()
            }).AllowAnonymous().WithMetadata(new RequestSizeLimitAttribute(1024));
        agent.MapPost("/rotation/finish", async (HttpContext context, AgentRegistration registrations, CancellationToken token) =>
            await registrations.FinishRotationAsync(Bearer(context), token) == AgentRegistrationStatus.Success ? Results.NoContent() : Results.Unauthorized())
            .AllowAnonymous();
    }

    private static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return context.Request.IsHttps && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
    }

}
