using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Observability;

internal static class InstanceAdministrationEndpoints
{
    internal static void MapInstanceAdministration(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/instance-admin");
        group.MapGet("/policy", async (InstancePolicyAdministration policy, CancellationToken token) => Results.Ok(await policy.GetAsync(token)));
        group.MapPut("/policy", async (UpdateInstancePolicyCommand command, InstancePolicyAdministration policy, CancellationToken token) =>
        {
            try { return await policy.UpdateAsync(command.Input, command.ActorId, token) is { } result ? Results.Ok(result) : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapGet("/services/{id:guid}", async (Guid id, ServiceInstanceAdministration instances, CancellationToken token, int page = 1) =>
            id.Version != 7 || page is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await instances.ListAsync(id, page, token)));
        group.MapPost("/services/{id:guid}/lookup", async (Guid id, Guid[] ids, ServiceInstanceAdministration instances, CancellationToken token) =>
            id.Version != 7 ? Results.BadRequest() : Results.Ok(await instances.GetInstancesAsync(id, ids, token)));
        group.MapPost("/{id:guid}", async (Guid id, ChangeInstanceCommand command, ServiceInstanceAdministration instances, CancellationToken token) =>
            Results.Ok(await instances.SetRetiredAsync(id, command.Version, command.Retired, command.ActorId, token)));
    }
}
