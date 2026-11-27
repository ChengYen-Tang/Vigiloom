using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

namespace Vigiloom.Security;

internal static class IntegrityScopeEndpoints
{
    internal static void MapPublicIntegrityScopes(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/agent-management/devices/{deviceId:guid}/integrity-scopes")
            .RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        group.MapGet("/", async (Guid deviceId, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await settings.ListAsync(deviceId, token) is { } scopes ? Results.Ok(scopes) : Results.NotFound();
        }).Produces<IReadOnlyList<IntegrityScopeView>>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        group.MapGet("/{scopeId:guid}", async (Guid deviceId, Guid scopeId, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return (await settings.ListAsync(deviceId, token))?.FirstOrDefault(x => x.Scope.Id == scopeId) is { } scope ? Results.Ok(scope) : Results.NotFound();
        }).Produces<IntegrityScopeView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        group.MapPost("/", async (Guid deviceId, IntegrityScopeInput input, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return ScopeResult(await settings.CreateAsync(deviceId, input, actor, token), deviceId, true); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<IntegrityScopeView>(201).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        group.MapPut("/{scopeId:guid}", async (Guid deviceId, Guid scopeId, IntegrityScopeUpdate input, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return ScopeResult(await settings.UpdateAsync(deviceId, scopeId, input.Policy, input.Version, actor, token), deviceId); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<IntegrityScopeView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        group.MapPost("/{scopeId:guid}/retire", async (Guid deviceId, Guid scopeId, IntegrityScopeRetire input, IntegrityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return ScopeResult(await settings.RetireAsync(deviceId, scopeId, input.Version, actor, token), deviceId); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<IntegrityScopeView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
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
