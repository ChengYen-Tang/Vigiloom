using System.Security.Cryptography;
using Tuf;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Vigiloom.Web.Api;

internal static class AgentPackageSourceEndpoints
{
    public static void MapAgentPackageSourceEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/package-source")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/trust", async (UpdateTrustStore trust, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await trust.GetStatusAsync(token));
        }).Produces<AgentPackageTrustView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Read the current Agent publisher trust root fingerprint");
        admin.MapPost("/trust", async (AgentPackageTrustRequest input, UpdateTrustStore trust, HttpContext context, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                if (input.Root is not { Length: > 0 and <= 1048576 })
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["root"] = ["The trusted root must be at most 1 MiB."] });
                try
                {
                    await trust.ProvisionForAdministratorAsync(input.Root, input.Sha256, actor, token);
                    return Results.NoContent();
                }
                catch (InvalidOperationException) { return Results.Conflict(); }
                catch (Exception error) when (error is ArgumentException or InvalidDataException or TufException or System.Text.Json.JsonException)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["root"] = ["The trusted root or independently supplied fingerprint is invalid."] });
                }
            }, allowApiCredential: false))
            .WithMetadata(new RequestSizeLimitAttribute(1500000))
            .Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).Produces(409).ProducesProblem(413).ProducesProblem(429)
            .WithSummary("Initialize publisher trust once using an independently authenticated root and fingerprint");
        admin.MapGet("/", async (AgentPackageSourceSettings settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await settings.GetAsync(token) is { } source ? Results.Ok(source) : Results.NoContent();
        }).Produces<AgentPackageSourceView>().Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Read the Agent publisher source without its credential");
        admin.MapPut("/", async (AgentPackageSourceUpdate input, AgentPackageSourceSettings settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!AgentPackageSourceSettings.Valid(input)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["source"] = ["Invalid publisher source settings."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try
                {
                    var saved = await settings.UpdateAsync(input, actor, token);
                    return saved is null ? Results.Conflict() : Results.Ok(saved);
                }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["credential"] = ["Clear or replace the credential when changing the publisher address."] }); }
            }, allowApiCredential: false);
        }).Produces<AgentPackageSourceView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).Produces(409).ProducesProblem(429)
            .WithSummary("Update the Agent publisher source and its write-only credential");
        admin.MapPost("/refresh", async (AgentPackageSourceSettings settings, UpdateTrustStore trust, HttpContext context, ILogger<AgentPackageSourceSettings> logger, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.RefreshAsync(trust, actor, token) ? Results.NoContent() : Results.Conflict(); }
                catch (InvalidOperationException) { return Results.Conflict(); }
                catch (OperationCanceledException error) when (!token.IsCancellationRequested)
                {
                    logger.LogWarning(error, "The Agent publisher catalogue request timed out.");
                    return Results.Problem(statusCode: 503, title: "The publisher catalogue is unavailable or could not be verified.");
                }
                catch (Exception error) when (error is HttpRequestException or TufException or InvalidDataException or CryptographicException or TimeoutException)
                {
                    logger.LogWarning(error, "The Agent publisher catalogue could not be verified.");
                    return Results.Problem(statusCode: 503, title: "The publisher catalogue is unavailable or could not be verified.");
                }
            }, allowApiCredential: false))
            .Produces(204).ProducesProblem(401).ProducesProblem(403).Produces(409).ProducesProblem(429).ProducesProblem(503)
            .WithSummary("Verify and refresh the configured Agent publisher catalogue");
    }
}
