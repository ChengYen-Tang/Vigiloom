using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.Api;

internal static class AgentHeartbeatEndpoint
{
    public const int MaximumBytes = 2048;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4
    };

    public static async Task<IResult> ReceiveAsync(HttpContext context, AgentIdentity agents, CancellationToken token)
    {
        if (context.Request.Headers.ContainsKey("Content-Encoding")) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumBytes + 1);
        try
        {
            var count = 0;
            while (count <= MaximumBytes)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(count, MaximumBytes + 1 - count), token);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            AgentHeartbeatReport? report = null;
            if (count > 0)
            {
                if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                try { report = JsonSerializer.Deserialize<AgentHeartbeatReport>(buffer.AsSpan(0, count), Options); }
                catch (JsonException) { return Results.BadRequest(); }
                if (report is null || report.Version != 1 || report.Epoch.Version != 7 || report.Sequence < 1 || report.Queue is { } queue && !queue.IsValid()) return Results.BadRequest();
            }
            return await agents.RecordHeartbeatAsync(AgentEndpoints.Bearer(context), report, token) switch
            {
                AgentIdentityStatus.Success => Results.NoContent(),
                AgentIdentityStatus.Conflict => Results.Conflict(),
                _ => Results.Unauthorized()
            };
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
