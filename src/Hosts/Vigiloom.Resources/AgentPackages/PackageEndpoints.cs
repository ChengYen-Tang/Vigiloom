using Vigiloom.Modules.Resources.Application.AgentPackages;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Resources.AgentPackages;

internal static class PackageEndpoints
{
    internal static void MapInternalPackages(this RouteGroupBuilder api)
    {
        api.MapGet("/agent-packages", async (int? page, PackageCatalog catalog, CancellationToken token) =>
            page is < 1 or > 20000 ? Results.BadRequest() : Results.Ok(await catalog.ListAsync(page ?? 1, token)));
        api.MapPost("/agent-packages/download", async (DownloadPackage request, PackageCatalog catalog, CancellationToken token) =>
            await catalog.DownloadAsync(request.Version, request.RuntimeIdentifier, token) ? Results.NoContent() : Results.NotFound());
        api.MapDelete("/agent-packages/{id:guid}/cache", async (Guid id, PackageCatalog catalog, CancellationToken token) =>
            Results.Ok(await catalog.RemoveAsync(id, token)));
        api.MapGet("/agents/{id:guid}/assignment", async (Guid id, AgentAssignments assignments, CancellationToken token) =>
            await assignments.GetAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound());
        api.MapGet("/agents/{id:guid}/target", async (Guid id, AgentAssignments assignments, CancellationToken token) =>
            await assignments.GetTargetAsync(id, token) is { } target ? Results.Ok(target) : Results.NoContent());
        api.MapPut("/agents/{id:guid}/assignment", async (Guid id, AgentVersionAssignmentCommand request, AgentAssignments assignments, CancellationToken token) =>
            Results.Ok(await assignments.SetAsync(id, request, token)));
        api.MapGet("/agents/{id:guid}/update/targets/{name}", async (Guid id, string name, AgentPackageAccess access, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var content = await access.ReadManifestAsync(id, name, token);
            return content is null ? Results.NotFound() : Results.File(content, "application/json");
        });
        api.MapGet("/agents/{id:guid}/update/packages/{name}", async (Guid id, string name, AgentPackageAccess access, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var stream = await access.OpenArchiveAsync(id, name, token);
            return stream is null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream");
        });
    }

    internal sealed record DownloadPackage(string Version, string RuntimeIdentifier);
}
