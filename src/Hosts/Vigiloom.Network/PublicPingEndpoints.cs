namespace Vigiloom.Network;

internal static class PublicPingEndpoints
{
    public static void MapPublicPingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/public/devices/{id:guid}/ping");
        group.MapGet("/", async (Guid id, PublicPing reader, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await reader.ReadAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).RequireRateLimiting("api-client");
        group.MapGet("/history", async (Guid id, DateTimeOffset from, DateTimeOffset to, PublicPing reader, HttpContext context, CancellationToken token) =>
        {
            if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from >= to || to - from > TimeSpan.FromDays(7)) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return await reader.HistoryAsync(id, from, to, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).RequireRateLimiting("api-client");
    }
}
