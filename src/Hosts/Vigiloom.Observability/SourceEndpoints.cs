using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Observability;

internal static class SourceEndpoints
{
    internal static void MapSourceEndpoints(this WebApplication app)
    {
        var sources = app.MapGroup("/internal/v1/observation-sources");
        sources.MapGet("/", async (SourceAdministration administration, CancellationToken token, int page = 1) =>
            page is < 1 or > 200 ? Results.BadRequest() : Results.Ok(await administration.ListAsync(page, token)));
        sources.MapPost("/", async (IssueServiceSourceCommand command, SourceAdministration administration, CancellationToken token) =>
        {
            if (command.ActorId.Version != 7 || string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 128) return Results.BadRequest();
            return Results.Ok(await administration.IssueAsync(command.Name, command.ActorId, token));
        });
        sources.MapPost("/{id:guid}/revoke", async (Guid id, RevokeServiceSourceCommand command, SourceAdministration administration, CancellationToken token) =>
        {
            if (id.Version != 7 || command.ActorId.Version != 7) return Results.BadRequest();
            return await administration.RevokeAsync(id, command.ActorId, token) ? Results.NoContent() : Results.NotFound();
        });
        sources.MapPost("/{id:guid}/rotate", async (Guid id, RotateServiceSourceCommand command, SourceAdministration administration, CancellationToken token) =>
        {
            if (id.Version != 7 || command.ActorId.Version != 7 || command.Version is not { Length: 8 }) return Results.BadRequest();
            return Results.Ok(await administration.RotateAsync(id, command.Version, command.ActorId, token));
        });
    }
}
