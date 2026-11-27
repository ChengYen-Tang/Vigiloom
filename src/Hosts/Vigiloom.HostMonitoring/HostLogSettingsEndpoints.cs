using System.Net.Http.Json;
using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Infrastructure.Authentication;

namespace Vigiloom.HostMonitoring;

internal static class HostLogSettingsEndpoints
{
    public static void MapHostLogSettingsEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/devices/{deviceId:guid}/log-sources")
            .RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        admin.MapGet("/", async (Guid deviceId, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
            return await settings.ListAsync(deviceId, token) is { } list ? Results.Ok(list) : Results.NotFound();
        }).Produces<IReadOnlyList<HostLogSourceView>>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        admin.MapGet("/{sourceId:guid}", async (Guid deviceId, Guid sourceId, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
            return (await settings.ListAsync(deviceId, token))?.FirstOrDefault(x => x.Settings.SourceId == sourceId) is { } source ? Results.Ok(source) : Results.NotFound();
        }).Produces<HostLogSourceView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        admin.MapPost("/", async (Guid deviceId, HostLogSourceCreate input, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
                try { return SourceResult(await settings.CreateAsync(deviceId, input, actor, token), deviceId, true); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<HostLogSourceView>(201).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        admin.MapPut("/{sourceId:guid}", async (Guid deviceId, Guid sourceId, HostLogSourceUpdate input, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
                try { return SourceResult(await settings.UpdateAsync(deviceId, sourceId, input, actor, token), deviceId); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<HostLogSourceView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        admin.MapPost("/{sourceId:guid}/retire", async (Guid deviceId, Guid sourceId, HostLogSourceRetire input, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
                try { return SourceResult(await settings.RetireAsync(deviceId, sourceId, input.Version, actor, token), deviceId); }
                catch (ArgumentException) { return Invalid(); }
            }, allowApiCredential: false);
        }).Produces<HostLogSourceView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        const string root = "/internal/v1/devices/{deviceId:guid}/log-sources";
        app.MapGet(root, async (Guid deviceId, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, CancellationToken token) =>
            await ResourceDevices.ExistsAsync(clients, deviceId, token) ? Results.Ok(await settings.ListAsync(deviceId, token)) : Results.NotFound());
        app.MapPost(root, async (Guid deviceId, HostLogSourceCreateCommand command, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, CancellationToken token) =>
        {
            if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
            try { return Results.Ok(await settings.CreateAsync(deviceId, command.Input, command.Actor, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        app.MapPost(root + "/{sourceId:guid}/update", async (Guid deviceId, Guid sourceId, HostLogSourceUpdateCommand command, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, CancellationToken token) =>
        {
            if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
            try { return Results.Ok(await settings.UpdateAsync(deviceId, sourceId, command.Input, command.Actor, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        app.MapPost(root + "/{sourceId:guid}/retire", async (Guid deviceId, Guid sourceId, HostLogSourceRetireCommand command, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, CancellationToken token) =>
        {
            if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
            try { return Results.Ok(await settings.RetireAsync(deviceId, sourceId, command.Version, command.Actor, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        app.MapGet("/api/v1/agent/log-settings", async (HttpContext context, HostMonitoringLogConfiguration settings, IHttpClientFactory clients, CancellationToken token) =>
        {
            var identity = (RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!;
            var snapshot = await settings.GetForAgentAsync(identity, token);
            var quota = await clients.CreateClient("operations").GetFromJsonAsync<LogQuotaSettings>("internal/v1/log-quota", token)
                ?? throw new InvalidOperationException("Operations returned empty log quota settings.");
            snapshot = AgentLogSettingsSnapshot.Create(snapshot.Sources, Math.Min(AgentLogBatchCodec.MaximumItems, Math.Min(quota.SourceBurst, quota.GlobalBurst)));
            var tag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
            context.Response.GetTypedHeaders().ETag = tag;
            context.Response.Headers.CacheControl = "private, no-store";
            return context.Request.GetTypedHeaders().IfNoneMatch?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true
                ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Ok(snapshot);
        }).Produces<AgentLogSettingsSnapshot>().Produces(304).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
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
