using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.Resources.Application.Publishing;

namespace Vigiloom.Resources.Publication;

internal static class TemplateEndpoints
{
    internal static void MapPublicTemplateManagement(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/admin/publications/templates").RequireAuthorization(WorkspaceAccess.Administrator);
        api.MapGet("/", async (int? page, PublicTemplates templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return page is < 1 or > 42949672 ? Results.BadRequest() : Results.Ok(await templates.ListAsync(page ?? 1, token));
        });
        api.MapGet("/default", async (PublicTemplates templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await templates.GetAsync(null, true, token) is { } row ? Results.Ok(row) : Results.NotFound();
        });
        api.MapGet("/{id:guid}", async (Guid id, PublicTemplates templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await templates.GetAsync(id, false, token) is { } row ? Results.Ok(row) : Results.NotFound();
        });
        api.MapPost("/", async (CreatePublicationTemplate input, PublicTemplates templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor => Response(await templates.CreateAsync(input, actor, token)), allowApiCredential: false);
        });
        api.MapPut("/{id:guid}/default", async (Guid id, SetDefaultPublicationTemplate input, PublicTemplates templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor => Response(await templates.SetDefaultAsync(id, input, actor, token)), allowApiCredential: false);
        });
        api.MapGet("/{id:guid}/impact", async (Guid id, int? page, TemplateChanges changes, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Response(await changes.ImpactAsync(id, page ?? 1, token));
        });
        api.MapPost("/{id:guid}/update-preview", async (Guid id, PublicationTemplateUpdate input, TemplateChanges changes, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor => Response(await changes.PreviewUpdateAsync(id, input, actor, token)), allowApiCredential: false);
        });
        api.MapPut("/{id:guid}", async (Guid id, ConfirmPublicationTemplateUpdate input, TemplateChanges changes, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor => Response(await changes.ConfirmUpdateAsync(id, input, actor, token)), allowApiCredential: false);
        });
        api.MapPost("/{id:guid}/retirement-preview", async (Guid id, PublicationTemplateRetirement input, TemplateChanges changes, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor => Response(await changes.PreviewRetireAsync(id, input, actor, token)), allowApiCredential: false);
        });
        api.MapPost("/{id:guid}/retire", async (Guid id, ConfirmPublicationTemplateRetirement input, TemplateChanges changes, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor => Status(await changes.ConfirmRetireAsync(id, input, actor, token)), allowApiCredential: false);
        });
    }

    private static IResult Response<T>(TemplateOperationResult<T> result) => result.Status switch
    {
        TemplateOperationStatus.Success => Results.Ok(result.Value),
        TemplateOperationStatus.Invalid => Results.BadRequest(),
        TemplateOperationStatus.NotFound => Results.NotFound(),
        _ => Results.Conflict()
    };

    private static IResult Status(TemplateOperationStatus status) => status switch
    {
        TemplateOperationStatus.Success => Results.NoContent(),
        TemplateOperationStatus.Invalid => Results.BadRequest(),
        TemplateOperationStatus.NotFound => Results.NotFound(),
        _ => Results.Conflict()
    };
}
