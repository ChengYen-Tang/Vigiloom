using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Publishing;

namespace Vigiloom.Web.Api;

internal static class PublicationEndpoints
{
    public static void MapPublicationEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin/publications").RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/templates", async (int? page, TemplateReader templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return page is < 1 or > TemplateReader.MaximumPage ? Results.BadRequest() : Results.Ok(await templates.ListAsync(page ?? 1, token));
        }).Produces<PublicationTemplatePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List publication templates");
        admin.MapGet("/templates/{id:guid}/impact", async (Guid id, int? page, TemplateReader templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (page is < 1 or > (TemplateImpact.MaximumDevices + TemplateImpact.PageSize - 1) / TemplateImpact.PageSize) return Results.BadRequest();
            var result = await templates.ImpactAsync(id, page ?? 1, token);
            if (result.Changed) return Results.Problem(statusCode: 409, title: "The template changed; refresh the preview.");
            if (result.TooMany) return Results.Problem(statusCode: 409, title: "Too many devices to preview safely.");
            return result.Impact is null ? Results.NotFound() : Results.Ok(result.Impact);
        }).Produces<PublicationImpact>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Preview devices assigned to a publication template");
        admin.MapGet("/{kind}/{id:guid}", async (string kind, Guid id, PublicationAdministration publications, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryKind(kind, out var resourceKind)) return Results.NotFound();
            return await publications.ReadAsync(resourceKind, id, token) is { } settings ? Results.Ok(settings) : Results.NotFound();
        }).Produces<PublicationSettings>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get publication settings");
        admin.MapGet("/{kind}/{id:guid}/preview", async (string kind, Guid id, PublicationPreviewReader preview, CancellationToken token) =>
        {
            if (!TryKind(kind, out var resourceKind)) return Results.NotFound();
            return await preview.ReadAsync(resourceKind, id, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<PublicationPreview>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Preview allowed publication status");
        admin.MapPut("/{kind}/{id:guid}", async (string kind, Guid id, PublicationChange change, PublicationAdministration publications, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryKind(kind, out var resourceKind)) return Results.NotFound();
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await publications.SetAsync(resourceKind, id, change, actor, token);
                return result.Status switch
                {
                    PublicationChangeStatus.Updated => Results.Ok(result.Settings),
                    PublicationChangeStatus.NotFound => Results.NotFound(),
                    PublicationChangeStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["publication"] = ["請確認公開名稱、政策及版本。名稱不可包含 IP 位址或過於複雜的符號組合。"] }),
                    _ => Results.Problem(statusCode: 409, title: "Publication settings changed or the resource is unavailable.")
                };
            }, allowApiCredential: false);
        }).Produces<PublicationSettings>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Change publication settings");

        var publicApi = app.MapGroup("/api/v1/public").AllowAnonymous().RequireRateLimiting("api-client");
        publicApi.MapGet("/devices", async (string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.DevicesAsync(search?.Trim(), page ?? 1, null, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicDevice>>().ProducesProblem(400).WithSummary("List published Ping states");
        publicApi.MapGet("/devices/{id:guid}", async (Guid id, PublicReader reader, CancellationToken token) =>
            await reader.DeviceAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .Produces<PublicDevice>().ProducesProblem(404).WithSummary("Get a published Ping state");
        publicApi.MapGet("/services", async (string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.ServicesAsync(search?.Trim(), page ?? 1, null, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicService>>().ProducesProblem(400).WithSummary("List published service health");
        publicApi.MapGet("/services/{id:guid}", async (Guid id, PublicReader reader, CancellationToken token) =>
            await reader.ServiceAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .Produces<PublicService>().ProducesProblem(404).WithSummary("Get a published service health state");
        publicApi.MapGet("/groups", async (string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.GroupsAsync(search?.Trim(), page ?? 1, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicGroup>>().ProducesProblem(400).WithSummary("List published groups and visible counts");
        publicApi.MapGet("/groups/{id:guid}", async (Guid id, PublicReader reader, CancellationToken token) =>
            await reader.GroupAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .Produces<PublicGroup>().ProducesProblem(404).WithSummary("Get a published group");
        publicApi.MapGet("/groups/{id:guid}/devices", async (Guid id, string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.DevicesAsync(search?.Trim(), page ?? 1, id, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicDevice>>().ProducesProblem(400).WithSummary("List published devices in a group");
        publicApi.MapGet("/groups/{id:guid}/services", async (Guid id, string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.ServicesAsync(search?.Trim(), page ?? 1, id, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicService>>().ProducesProblem(400).WithSummary("List published services in a group");
    }

    private static bool Valid(string? search, int? page) => search?.Length is null or <= 256 && page is null or >= 1 and <= PublicReader.MaximumPage;

    internal static bool TryKind(string text, out PublicationResourceKind kind)
    {
        kind = text switch { "devices" => PublicationResourceKind.Device, "services" => PublicationResourceKind.Service, "groups" => PublicationResourceKind.Group, _ => 0 };
        return kind != 0;
    }
}
