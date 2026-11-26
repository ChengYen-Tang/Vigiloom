using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;
using Vigiloom.Modules.SecurityAnalysis.Application.Baseline;
using Vigiloom.Web.HostMonitoring;

namespace Vigiloom.Web.Api;

internal static class BaselineSettingsEndpoints
{
    public static void MapBaselineSettingsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/admin/devices/{deviceId:guid}/baseline-exceptions", async (Guid deviceId, BaselineExceptionInput input,
            BaselineAccess exceptions, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return await ApiMutation.ExecuteAsync(context, async actor => (await exceptions.RecordAsync(deviceId, input.SourceKey,
                input.RevisionId, input.Reason, actor, token)) switch
            {
                BaselineExceptionStatus.Recorded => Results.NoContent(),
                BaselineExceptionStatus.NotFound => Results.NotFound(),
                BaselineExceptionStatus.VersionChanged => Results.Problem(statusCode: 409, title: "The selected baseline revision is no longer active."),
                _ => Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Invalid baseline exception explanation."] })
            }, allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Record an administrator explanation for a baseline revision");

        app.MapGet("/api/v1/devices/{deviceId:guid}/baseline", async (Guid deviceId, BaselineAccess query, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await query.GetAsync(deviceId, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<DeviceBaselineView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

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
