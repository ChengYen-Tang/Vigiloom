using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;

namespace Vigiloom.Operations;

internal static class TimelineEndpoints
{
    internal static void MapTimelineEndpoints(this WebApplication app)
    {
        app.MapGet("/internal/v1/events", async (DateTimeOffset from, DateTimeOffset until, Guid? groupId, string? source,
            int? page, HttpContext context, TimelineReader reader, IHttpClientFactory clients, CancellationToken token) =>
        {
            var query = new EventTimelineQuery(from, until, groupId, source, page ?? 1);
            if (!TimelineReader.Valid(query)) return Results.BadRequest();
            GroupMembers? members = null;
            if (groupId is { } id)
            {
                try
                {
                    using var response = await clients.CreateClient("Resources").GetAsync($"internal/v1/groups/{id:D}/members", token);
                    if (response.StatusCode == HttpStatusCode.NotFound) members = new(id, [], []);
                    else if (!response.IsSuccessStatusCode) return Results.StatusCode(503);
                    else members = await response.Content.ReadFromJsonAsync<GroupMembers>(cancellationToken: token);
                    if (members is null || members.GroupId != id) return Results.StatusCode(503);
                }
                catch (HttpRequestException) { return Results.StatusCode(503); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Results.StatusCode(503); }
                catch (System.Text.Json.JsonException) { return Results.StatusCode(503); }
            }
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await reader.GetAsync(query, members, token));
        });
    }
}
