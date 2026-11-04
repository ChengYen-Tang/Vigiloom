using Google.Protobuf;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Web.Telemetry;

/// <summary>Maps committed exports to the OTLP/HTTP success and failure contract.</summary>
internal static class OtlpResponse
{
    internal const string ContentType = "application/x-protobuf";

    internal static IResult Failure(int status, string message) => new ProtobufResult(
        status, new Google.Rpc.Status { Message = message }.ToByteArray());

    internal static IResult FromResult<T>(HttpContext context, ExportResult result, Func<ExportResult, T> response)
        where T : IMessage
    {
        if (result.Status is ExportStatus.Complete or ExportStatus.Partial)
            return new ProtobufResult(StatusCodes.Status200OK, response(result).ToByteArray());
        if (result.Status == ExportStatus.Unauthorized)
            return Failure(StatusCodes.Status401Unauthorized, "A valid service source credential is required.");
        if (result.Status == ExportStatus.Retry)
        {
            if (result.RetryAfter is { } delay)
                context.Response.Headers.RetryAfter = Math.Clamp((int)Math.Ceiling(delay.TotalSeconds), 1, 3600).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Failure(StatusCodes.Status503ServiceUnavailable, "Telemetry is temporarily unavailable; retry later.");
        }
        return result.Status == ExportStatus.BatchTooLarge
            ? Failure(StatusCodes.Status400BadRequest, "Batch exceeds the effective quota burst; reduce the exporter batch size.")
            : Failure(StatusCodes.Status400BadRequest, "Invalid telemetry request.");
    }

    private sealed record ProtobufResult(int Status, byte[] Body) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = Status;
            context.Response.ContentType = ContentType;
            context.Response.ContentLength = Body.Length;
            await context.Response.Body.WriteAsync(Body, context.RequestAborted);
        }
    }
}
