using Google.Protobuf;

namespace Vigiloom.Gateway.Telemetry;

internal static class OtlpErrors
{
    public static async Task WriteAsync(HttpContext context, int status)
    {
        var body = new Google.Rpc.Status { Message = status == StatusCodes.Status503ServiceUnavailable
            ? "Telemetry receiver is unavailable; retry later." : "Telemetry request was rejected." }.ToByteArray();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/x-protobuf";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }
}
