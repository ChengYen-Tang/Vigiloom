using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vigiloom.BuildingBlocks.Operations;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Internal-only endpoints; do not expose these routes through the Gateway.</summary>
public static class WorkHistoryEndpoints
{
    public static void MapWorkHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/v1/work-history/count", async (WorkHistoryRequest request, WorkHistoryRetention history, CancellationToken token) =>
        {
            if (request.Cutoff.Offset != TimeSpan.Zero || request.ExcludedDatabases is null) return Results.BadRequest();
            var database = await history.DatabaseAsync(token);
            var count = request.ExcludedDatabases.Contains(database, StringComparer.OrdinalIgnoreCase) ? 0 : await history.CountAsync(request.Cutoff, token);
            return Results.Ok(new BackgroundHistoryResult(database, count));
        });
        app.MapPost("/internal/v1/work-history/delete", async (WorkHistoryRequest request, WorkHistoryRetention history, CancellationToken token) =>
        {
            if (request.Cutoff.Offset != TimeSpan.Zero || request.ExcludedDatabases is null || request.BatchSize is < 1 or > 500) return Results.BadRequest();
            var database = await history.DatabaseAsync(token);
            var count = request.ExcludedDatabases.Contains(database, StringComparer.OrdinalIgnoreCase) ? 0 : await history.DeleteBatchAsync(request.Cutoff, request.BatchSize, token);
            return Results.Ok(new BackgroundHistoryResult(database, count));
        });
    }
}
