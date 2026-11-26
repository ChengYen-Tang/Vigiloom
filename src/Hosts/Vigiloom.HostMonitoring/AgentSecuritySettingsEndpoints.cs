using System.Net.Http.Json;
using System.Net;
using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Baseline;

namespace Vigiloom.HostMonitoring;

internal static class AgentSecuritySettingsEndpoints
{
    public static void MapAgentSecuritySettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent/integrity-status/{scopeId:guid}", async (Guid scopeId, HttpContext context, IHttpClientFactory clients, CancellationToken token) =>
        {
            if (scopeId.Version != 7) return Results.BadRequest();
            var identity = (RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!;
            using var response = await clients.CreateClient("security").PostAsJsonAsync($"internal/v1/agent-integrity-status/{scopeId:D}", identity, token);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            response.EnsureSuccessStatusCode();
            var status = await response.Content.ReadFromJsonAsync<IntegrityScopeStatus>(cancellationToken: token)
                ?? throw new InvalidOperationException("Security service returned empty integrity status.");
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(status);
        });
        app.MapGet("/api/v1/agent/baseline-settings", async (HttpContext context, IHttpClientFactory clients, CancellationToken token) =>
        {
            var identity = (RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!;
            var snapshot = await clients.CreateClient("security").GetFromJsonAsync<BaselineSettingsSnapshot>(
                $"internal/v1/agent-settings/{identity.DeviceId:D}/baseline", token)
                ?? throw new InvalidOperationException("Security service returned empty baseline settings.");
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(snapshot);
        });
        app.MapGet("/api/v1/agent/inventory-settings", async (int? contract, HttpContext context, IHttpClientFactory clients, CancellationToken token) =>
        {
            if (contract is not (null or 2)) return Results.BadRequest();
            var identity = (RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!;
            var snapshot = await clients.CreateClient("security").GetFromJsonAsync<InventorySettingsSnapshot>(
                $"internal/v1/agent-settings/{identity.DeviceId:D}/inventory", token)
                ?? throw new InvalidOperationException("Security service returned empty inventory settings.");
            if (contract is null && snapshot.Settings.CollectCommandLine)
                snapshot = InventorySettingsSnapshot.Create(new(snapshot.Settings.ScanIntervalSeconds));
            return Reply(context, snapshot.Revision, snapshot);
        });
        app.MapGet("/api/v1/agent/integrity-settings", async (HttpContext context, IHttpClientFactory clients, CancellationToken token) =>
        {
            var identity = (RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!;
            var snapshot = await clients.CreateClient("security").GetFromJsonAsync<IntegritySettingsSnapshot>(
                $"internal/v1/agent-settings/{identity.DeviceId:D}/integrity", token)
                ?? throw new InvalidOperationException("Security service returned empty integrity settings.");
            return Reply(context, snapshot.Revision, snapshot);
        });
    }

    private static IResult Reply<T>(HttpContext context, string revision, T snapshot)
    {
        var tag = new EntityTagHeaderValue($"\"{revision}\"");
        context.Response.GetTypedHeaders().ETag = tag;
        context.Response.Headers.CacheControl = "private, no-store";
        return context.Request.GetTypedHeaders().IfNoneMatch?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true
            ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Ok(snapshot);
    }
}
