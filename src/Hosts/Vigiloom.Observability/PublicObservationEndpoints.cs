using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Observability;

internal static class PublicObservationEndpoints
{
    internal static void MapPublicObservation(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/public-observation");
        group.MapPost("/versions", async (Guid[] ids, IDbContextFactory<ServiceObservabilityDbContext> contexts, CancellationToken token) =>
        {
            if (ids.Length > 250 || ids.Any(id => id.Version != 7)) return Results.BadRequest();
            await using var db = await contexts.CreateDbContextAsync(token);
            var rows = await (from service in db.Services.AsNoTracking()
                join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                where ids.Contains(service.Id)
                select new PublicObservationVersions(service.Id, service.Version, source.Version)).ToArrayAsync(token);
            return Results.Ok(rows);
        });
        group.MapPost("/statuses", async (Guid[] ids, HealthStatusReader health, CancellationToken token) =>
        {
            try { return Results.Ok(await health.ReadManyAsync(ids, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapGet("/time", async (IDbContextFactory<ServiceObservabilityDbContext> contexts, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            return Results.Ok(await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token));
        });
    }
}
