using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class BaselineSettingsEndpoints
{
    public static void MapBaselineSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent/baseline-settings", async (AgentIdentity identity, IBaselineSource sources,
            HttpContext context, CancellationToken token) =>
        {
            if (await identity.AuthenticateAsync(AgentEndpoints.Bearer(context), token) is null) return Results.Unauthorized();
            var snapshots = await Task.WhenAll(sources.GetActiveAsync(BaselineSources.LinuxShadow, token),
                sources.GetActiveAsync(BaselineSources.WindowsNetworkSelection, token));
            var checks = snapshots.Where(x => x is { Availability: BaselineSourceAvailability.Ready, SourceId: not null,
                    RevisionId: not null, ActivationVersion: not null, Check: not null })
                .Select(x => new BaselineSelection(x.SourceId!.Value, x.RevisionId!.Value, x.ActivationVersion!, x.Check!)).ToArray();
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(BaselineSettingsSnapshot.Create(BaselineSettingsSnapshot.DefaultIntervalSeconds, checks));
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<BaselineSettingsSnapshot>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
    }
}
