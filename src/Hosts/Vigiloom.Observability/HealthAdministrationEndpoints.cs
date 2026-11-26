using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Observability;

internal static class HealthAdministrationEndpoints
{
    internal static void MapHealthAdministration(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/health-admin");
        group.MapGet("/networks", async (HealthNetworkPolicyAdministration networks, CancellationToken token) => Results.Ok(await networks.GetAsync(token)));
        group.MapGet("/networks/history", async (HealthNetworkPolicyAdministration networks, CancellationToken token, int page = 1) =>
            page is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await networks.ListRevisionsAsync(page, token)));
        group.MapPut("/networks", async (UpdateHealthNetworkCommand command, HealthNetworkPolicyAdministration networks, CancellationToken token) =>
        {
            try { return await networks.UpdateAsync(command.Input, command.ActorId, token) is { } result ? Results.Ok(result) : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapGet("/targets", async (Guid serviceId, HealthTargetAdministration targets, CancellationToken token, int page = 1) =>
            serviceId.Version != 7 || page is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await targets.ListAsync(serviceId, page, token)));
        group.MapGet("/targets/{id:guid}", async (Guid id, HealthTargetAdministration targets, CancellationToken token) =>
            await targets.GetAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/observations", async (Guid serviceId, HealthObservationReader observations, CancellationToken token, int page = 1) =>
            serviceId.Version != 7 || page is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await observations.LatestAsync(serviceId, page, token)));
        group.MapPost("/targets", async (CreateHealthTargetCommand command, HealthTargetAdministration targets, CancellationToken token) =>
            Results.Ok(await targets.CreateAsync(command.Input, command.ActorId, token)));
        group.MapPut("/targets/{id:guid}", async (Guid id, UpdateHealthTargetCommand command, HealthTargetAdministration targets, CancellationToken token) =>
            Results.Ok(await targets.UpdateAsync(id, command.Input, command.ActorId, token)));
    }
}
