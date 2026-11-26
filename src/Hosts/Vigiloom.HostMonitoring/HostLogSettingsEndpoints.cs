using System.Net.Http.Json;
using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal static class HostLogSettingsEndpoints
{
    public static void MapHostLogSettingsEndpoints(this WebApplication app)
    {
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
        });
    }
}
