using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class BaselineSettingsEndpoints
{
    public static void MapBaselineSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent/baseline-settings", async (AgentInventorySettings inventory, IBaselineSource sources,
            HttpContext context, CancellationToken token) =>
        {
            var inventoryPolicy = await inventory.GetAsync(AgentEndpoints.Bearer(context), token);
            if (inventoryPolicy is null) return Results.Unauthorized();
            var checks = new List<BaselineSelection>();
            foreach (var key in await sources.GetKeysAsync(token))
            {
                var selected = await sources.GetActiveAsync(key, token);
                if (selected is { Availability: BaselineSourceAvailability.Ready, SourceId: not null,
                        RevisionId: not null, ActivationVersion: not null, Check: not null })
                    checks.Add(new(selected.SourceId.Value, selected.RevisionId.Value, selected.ActivationVersion, selected.Check));
            }
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(BaselineSettingsSnapshot.Create(inventoryPolicy.Settings.ScanIntervalSeconds, checks));
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<BaselineSettingsSnapshot>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
    }
}
