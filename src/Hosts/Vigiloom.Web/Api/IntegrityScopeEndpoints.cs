using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;
using Microsoft.Net.Http.Headers;

namespace Vigiloom.Web.Api;

internal static class IntegrityScopeEndpoints
{
    public static void MapIntegrityScopeEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/devices/{deviceId:guid}/integrity-scopes")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/", async (Guid deviceId, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await settings.ListAsync(deviceId, token) is { } scopes ? Results.Ok(scopes) : Results.NotFound();
        }).Produces<IReadOnlyList<IntegrityScopeView>>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        admin.MapGet("/{scopeId:guid}", async (Guid deviceId, Guid scopeId, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var scopes = await settings.ListAsync(deviceId, token);
            return scopes?.FirstOrDefault(x => x.Scope.Id == scopeId) is { } scope ? Results.Ok(scope) : Results.NotFound();
        }).Produces<IntegrityScopeView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        admin.MapPost("/", async (Guid deviceId, IntegrityScopeInput input, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return ScopeResult(await settings.CreateAsync(deviceId, input, actor, token), deviceId, true); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<IntegrityScopeView>(201).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        admin.MapPut("/{scopeId:guid}", async (Guid deviceId, Guid scopeId, IntegrityScopeUpdate input, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return ScopeResult(await settings.UpdateAsync(deviceId, scopeId, input.Policy, input.Version, actor, token), deviceId); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<IntegrityScopeView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        admin.MapPost("/{scopeId:guid}/retire", async (Guid deviceId, Guid scopeId, IntegrityScopeRetire input, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return ScopeResult(await settings.RetireAsync(deviceId, scopeId, input.Version, actor, token), deviceId); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<IntegrityScopeView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);

        app.MapGet("/api/v1/agent/integrity-settings", async (AgentIntegritySettings settings, HttpContext context, CancellationToken token) =>
        {
            var snapshot = await settings.GetAsync(AgentEndpoints.Bearer(context), token);
            if (snapshot is null) return Results.Unauthorized();
            var tag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
            context.Response.GetTypedHeaders().ETag = tag;
            context.Response.Headers.CacheControl = "private, no-store";
            if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Ok(snapshot);
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<IntegritySettingsSnapshot>().Produces(304).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);

        app.MapGet("/api/v1/agent/integrity-status/{scopeId:guid}", async (Guid scopeId, AgentIntegrityStatus status, HttpContext context, CancellationToken token) =>
        {
            if (scopeId.Version != 7) return Results.BadRequest();
            context.Response.Headers.CacheControl = "private, no-store";
            try { return await status.GetAsync(AgentEndpoints.Bearer(context), scopeId, token) is { } value ? Results.Ok(value) : Results.Unauthorized(); }
            catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<IntegrityScopeStatus>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
    }

    private static IResult Invalid() => Results.ValidationProblem(new Dictionary<string, string[]> { ["scope"] = ["完整性監控範圍設定無效。"] });

    private static IResult ScopeResult(IntegrityConfigurationResult result, Guid deviceId, bool created = false) => result.Status switch
    {
        IntegrityConfigurationStatus.Success when result.Scope is { } scope => created
            ? Results.Created($"/api/v1/agent-management/devices/{deviceId:D}/integrity-scopes/{scope.Scope.Id:D}", scope)
            : Results.Ok(scope),
        IntegrityConfigurationStatus.NotFound => Results.NotFound(),
        IntegrityConfigurationStatus.Conflict => Results.Problem(statusCode: 409, title: "Scope settings changed; reload and try again."),
        IntegrityConfigurationStatus.LimitReached => Results.Problem(statusCode: 409, title: "The device scope limit has been reached."),
        _ => throw new InvalidOperationException("Unknown scope configuration result.")
    };
}
