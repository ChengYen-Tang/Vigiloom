using System.Buffers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Web.Api;

internal static class ServiceObservationEndpoint
{
    public const int MaximumBytes = 2048;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4
    };

    public static async Task<IResult> ReceiveAsync(HttpContext context, CandidateDiscovery candidates, CancellationToken token)
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
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            if (count > MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            ServiceObservation? observation;
            try { observation = JsonSerializer.Deserialize<ServiceObservation>(buffer.AsSpan(0, count), Options); }
            catch (JsonException) { return Results.BadRequest(); }
            if (observation is null || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var sourceId))
                return Results.BadRequest();
            try
            {
                var result = await candidates.ObserveAsync(sourceId, observation.ServiceKey, observation.Environment, observation.DisplayName, token);
                return result switch
                {
                    CandidateObservation.Recorded or CandidateObservation.AlreadyApproved => Results.NoContent(),
                    CandidateObservation.SourceUnavailable => Results.Unauthorized(),
                    _ => Results.Problem(statusCode: 503, title: "Discovery is unavailable; retry later.")
                };
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
