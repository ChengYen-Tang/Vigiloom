using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Observability;

internal static class CollectorEndpoints
{
    internal static void MapCollectorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/collectors");
        group.MapGet("/", async (CollectorAdministration collectors, CancellationToken token, int page = 1) =>
            page is < 1 or > 2000 ? Results.BadRequest() : Results.Ok(await collectors.ListAsync(page, token)));
        group.MapGet("/{id:guid}", async (Guid id, CollectorAdministration collectors, CancellationToken token) =>
            await collectors.GetAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/{id:guid}/routes", async (Guid id, CollectorAdministration collectors, CancellationToken token) =>
            Results.Ok(await collectors.RoutesAsync(id, token)));
        group.MapGet("/observations/{id:guid}", async (Guid id, CollectorObservations observations, CancellationToken token, int page = 1) =>
            page is < 1 or > 2000 ? Results.BadRequest() : await observations.QueryAsync(id, page, token) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/{id:guid}/sources/{sourceId:guid}/instances/{instanceId:guid}/counters", async (Guid id, Guid sourceId, Guid instanceId, CollectorCounters counters, CancellationToken token) =>
            await counters.QueryAsync(id, sourceId, instanceId, token) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapPost("/", async (RegisterCollectorCommand command, CollectorAdministration collectors, CancellationToken token) =>
        {
            try { return Results.Ok(await collectors.RegisterAsync(command.Name, command.ServiceId, command.DedicatedSourceConfirmed, command.ActorId, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapPost("/{id:guid}/routes", async (Guid id, SetCollectorRouteCommand command, CollectorAdministration collectors, CancellationToken token) =>
        {
            try { return Results.Ok(await collectors.SetRouteAsync(id, command.SourceId, command.Version, command.Exporter, command.Add, command.ActorId, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
    }
}
