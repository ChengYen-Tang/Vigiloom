using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Security;

internal static class InventorySettingsEndpoints
{
    internal static void MapPublicInventorySettings(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/inventory-settings")
            .RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        admin.MapGet("/platform", (InventoryConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, null, context, token))
            .Produces<InventorySettingsView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        admin.MapPut("/platform", (InventorySettingsUpdate input, InventoryConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, null, input, context, token))
            .Produces<InventorySettingsView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429);
        admin.MapGet("/devices/{deviceId:guid}", (Guid deviceId, InventoryConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, deviceId, context, token))
            .Produces<InventorySettingsView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        admin.MapPut("/devices/{deviceId:guid}", (Guid deviceId, InventorySettingsUpdate input, InventoryConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, deviceId, input, context, token))
            .Produces<InventorySettingsView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
    }

    private static async Task<IResult> ReadAsync(InventoryConfiguration settings, Guid? deviceId, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        return await settings.GetAsync(deviceId, token) is { } view ? Results.Ok(view) : Results.NotFound();
    }

    private static async Task<IResult> UpdateAsync(InventoryConfiguration settings, Guid? deviceId, InventorySettingsUpdate input, HttpContext context, CancellationToken token)
    {
        if (!InventoryConfiguration.Valid(input?.Overrides))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["interval"] = ["盤點間隔超出允許範圍。"] });
        if (deviceId is not null && await settings.GetAsync(deviceId, token) is null) return Results.NotFound();
        return await ApiMutation.ExecuteAsync(context, async actor =>
            await settings.UpdateAsync(deviceId, input!, actor, token) is { } saved ? Results.Ok(saved) :
                Results.Problem(statusCode: 409, title: "Inventory settings changed; reload and try again."), allowApiCredential: false);
    }
}
