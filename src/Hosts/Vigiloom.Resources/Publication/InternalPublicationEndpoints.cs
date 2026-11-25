using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Application.Publishing;

namespace Vigiloom.Resources.Publication;

internal static class InternalPublicationEndpoints
{
    public static void MapInternalPublicationManagement(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/publication-management");
        group.MapGet("/templates", async (int page, PublicTemplates templates, CancellationToken token) =>
            page is < 1 or > 42949672 ? Results.BadRequest() : Results.Ok(await templates.ListAsync(page, token)));
        group.MapGet("/templates/default", async (PublicTemplates templates, CancellationToken token) =>
            await templates.GetAsync(null, true, token) is { } item ? Results.Ok(item) : Results.NotFound());
        group.MapGet("/templates/{id:guid}", async (Guid id, PublicTemplates templates, CancellationToken token) =>
            await templates.GetAsync(id, false, token) is { } item ? Results.Ok(item) : Results.NotFound());
        group.MapGet("/templates/{id:guid}/impact", async (Guid id, int page, TemplateChanges changes, CancellationToken token) =>
            Results.Ok(await changes.ImpactAsync(id, page, token)));
        group.MapPost("/templates", async (PublicationManagementRequest<CreatePublicationTemplate> request, PublicTemplates templates, CancellationToken token) =>
            Results.Ok(await templates.CreateAsync(request.Input, request.ActorId, token)));
        group.MapPut("/templates/{id:guid}/default", async (Guid id, PublicationManagementRequest<SetDefaultPublicationTemplate> request, PublicTemplates templates, CancellationToken token) =>
            Results.Ok(await templates.SetDefaultAsync(id, request.Input, request.ActorId, token)));
        group.MapPost("/templates/{id:guid}/update-preview", async (Guid id, PublicationManagementRequest<PublicationTemplateUpdate> request, TemplateChanges changes, CancellationToken token) =>
            Results.Ok(await changes.PreviewUpdateAsync(id, request.Input, request.ActorId, token)));
        group.MapPut("/templates/{id:guid}", async (Guid id, PublicationManagementRequest<ConfirmPublicationTemplateUpdate> request, TemplateChanges changes, CancellationToken token) =>
            Results.Ok(await changes.ConfirmUpdateAsync(id, request.Input, request.ActorId, token)));
        group.MapPost("/templates/{id:guid}/retirement-preview", async (Guid id, PublicationManagementRequest<PublicationTemplateRetirement> request, TemplateChanges changes, CancellationToken token) =>
            Results.Ok(await changes.PreviewRetireAsync(id, request.Input, request.ActorId, token)));
        group.MapPost("/templates/{id:guid}/retire", async (Guid id, PublicationManagementRequest<ConfirmPublicationTemplateRetirement> request, TemplateChanges changes, CancellationToken token) =>
            Results.Ok(await changes.ConfirmRetireAsync(id, request.Input, request.ActorId, token)));
        group.MapGet("/{kind}/{id:guid}", async (string kind, Guid id, PublicationSettingsManager manager, CancellationToken token) =>
            TryKind(kind, out var type) && await manager.GetAsync(type, id, token) is { } item ? Results.Ok(item) : Results.NotFound());
        group.MapPut("/{kind}/{id:guid}", async (string kind, Guid id, PublicationManagementRequest<PublicationChange> request, PublicationSettingsManager manager, CancellationToken token) =>
            TryKind(kind, out var type) ? Results.Ok(await manager.SetAsync(type, id, request.Input, request.ActorId, token)) : Results.NotFound());
    }

    private static bool TryKind(string text, out PublicationResourceKind kind)
    {
        kind = text switch { "devices" => PublicationResourceKind.Device, "services" => PublicationResourceKind.Service, "groups" => PublicationResourceKind.Group, _ => 0 };
        return kind != 0;
    }
}
