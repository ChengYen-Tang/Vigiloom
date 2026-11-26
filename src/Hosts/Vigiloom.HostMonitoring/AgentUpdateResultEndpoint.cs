using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal static class AgentUpdateResultEndpoint
{
    public const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false, MaxDepth = 4
    };

    public static async Task<IResult> ReceiveAsync(HttpContext context, HostUpdateResults results, CancellationToken token)
    {
        if (context.Request.Headers.ContainsKey("Content-Encoding") || !context.Request.HasJsonContentType())
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumBytes + 1);
        try
        {
            var count = 0;
            try
            {
                while (count <= MaximumBytes)
                {
                    var read = await context.Request.Body.ReadAsync(buffer.AsMemory(count, MaximumBytes + 1 - count), token);
                    if (read == 0) break;
                    count += read;
                }
            }
            catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            if (count > MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            AgentUpdateResult? report;
            try { report = JsonSerializer.Deserialize<AgentUpdateResult>(buffer.AsSpan(0, count), Options); }
            catch (JsonException) { return Results.BadRequest(); }
            if (report is null || !report.IsValid()) return Results.BadRequest();
            return await results.ReceiveAsync((RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!, report, token) switch
            {
                AgentUpdateResultStatus.Success => Results.NoContent(),
                AgentUpdateResultStatus.Invalid => Results.BadRequest(),
                AgentUpdateResultStatus.Conflict => Results.Conflict(),
                _ => Results.Unauthorized()
            };
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
