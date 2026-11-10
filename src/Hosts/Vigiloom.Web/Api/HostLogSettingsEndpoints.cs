using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class HostLogSettingsEndpoints
{
    public static void MapHostLogSettingsEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/devices/{deviceId:guid}/log-sources")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/", async (Guid deviceId, HostLogConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await settings.ListAsync(deviceId, token) is { } list ? Results.Ok(list) : Results.NotFound();
        }).Produces<IReadOnlyList<HostLogSourceView>>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        admin.MapGet("/{sourceId:guid}", async (Guid deviceId, Guid sourceId, HostLogConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var list = await settings.ListAsync(deviceId, token);
            return list?.FirstOrDefault(x => x.Settings.SourceId == sourceId) is { } source ? Results.Ok(source) : Results.NotFound();
        }).Produces<HostLogSourceView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        admin.MapPost("/", async (Guid deviceId, HostLogSourceCreate input, HostLogConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return SourceResult(await settings.CreateAsync(deviceId, input, actor, token), deviceId, created: true); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<HostLogSourceView>(201).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        admin.MapPut("/{sourceId:guid}", async (Guid deviceId, Guid sourceId, HostLogSourceUpdate input, HostLogConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return SourceResult(await settings.UpdateAsync(deviceId, sourceId, input, actor, token), deviceId); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<HostLogSourceView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        admin.MapPost("/{sourceId:guid}/retire", async (Guid deviceId, Guid sourceId, HostLogSourceRetire input, HostLogConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return SourceResult(await settings.RetireAsync(deviceId, sourceId, input.Version, actor, token), deviceId); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<HostLogSourceView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);

        app.MapGet("/api/v1/agent/log-settings", async (HostLogConfiguration settings, PolicyAdministration quota, HttpContext context, CancellationToken token) =>
        {
            var snapshot = await settings.GetForAgentAsync(AgentEndpoints.Bearer(context), token);
            if (snapshot is null) return Results.Unauthorized();
            var limits = (await quota.GetAsync(token)).Values;
            snapshot = AgentLogSettingsSnapshot.Create(snapshot.Sources,
                Math.Min(AgentLogBatchCodec.MaximumItems, Math.Min(limits.SourceBurst, limits.GlobalBurst)));
            var tag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
            context.Response.GetTypedHeaders().ETag = tag;
            context.Response.Headers.CacheControl = "private, no-store";
            if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Ok(snapshot);
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<AgentLogSettingsSnapshot>().Produces(304).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
    }

    private static IResult Invalid() => Results.ValidationProblem(new Dictionary<string, string[]> { ["source"] = ["日誌來源設定無效。"] });

    private static IResult SourceResult(HostLogConfigurationResult result, Guid deviceId, bool created = false) => result.Status switch
    {
        HostLogConfigurationStatus.Success when result.Source is { } source => created
            ? Results.Created($"/api/v1/agent-management/devices/{deviceId:D}/log-sources/{source.Settings.SourceId:D}", source)
            : Results.Ok(source),
        HostLogConfigurationStatus.NotFound => Results.NotFound(),
        HostLogConfigurationStatus.Conflict => Results.Problem(statusCode: 409, title: "Source settings changed; reload and try again."),
        HostLogConfigurationStatus.LimitReached => Results.Problem(statusCode: 409, title: "The device source limit has been reached."),
        _ => throw new InvalidOperationException("Unknown source configuration result.")
    };
}
