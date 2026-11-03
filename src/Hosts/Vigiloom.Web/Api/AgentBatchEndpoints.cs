using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentBatchEndpoints
{
    public static void MapAgentBatchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/agent/batches", ReceiveAsync)
            .RequireAuthorization("Agent").RequireRateLimiting("agent-transport")
            .WithMetadata(AgentTransportEndpoint.Instance, new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AgentBatchCodec.MaximumBytes))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(503)
            .WithSummary("Commit an authenticated, validated Agent runtime batch");
    }

    private static async Task<IResult> ReceiveAsync(HttpContext context, AgentBatchReceiver receiver, CancellationToken token)
    {
        var encodings = context.Request.Headers.ContentEncoding;
        var gzip = encodings.Count == 1 && string.Equals(encodings[0], "gzip", StringComparison.OrdinalIgnoreCase);
        if (!context.Request.HasJsonContentType() || encodings.Count != 0 && !gzip)
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > AgentBatchCodec.MaximumBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var encoded = new MemoryStream();
        using var decoded = gzip ? new MemoryStream() : null;
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            // Read the complete wire body before decoding: a gzip decoder can stop
            // at trailing data without consuming an oversized chunked request.
            try
            {
                if (!await ReadBoundedAsync(context.Request.Body, encoded, chunk, token))
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var body = encoded;
            if (gzip)
            {
                encoded.Position = 0;
                using var decompressor = new GZipStream(encoded, CompressionMode.Decompress, leaveOpen: true);
                try
                {
                    if (!await ReadBoundedAsync(decompressor, decoded!, chunk, token))
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
                catch (InvalidDataException) { return Results.BadRequest(); }
                body = decoded!;
            }
            var result = await receiver.ReceiveAsync(AgentEndpoints.Bearer(context), body.GetBuffer().AsMemory(0, (int)body.Length), token);
            return result switch
            {
                BatchReceiptStatus.Accepted or BatchReceiptStatus.AlreadyAccepted => Results.NoContent(),
                BatchReceiptStatus.Unauthorized => Results.Unauthorized(),
                BatchReceiptStatus.Invalid => Results.BadRequest(),
                BatchReceiptStatus.Conflict => Results.Conflict(),
                BatchReceiptStatus.CapacityExceeded or BatchReceiptStatus.ClockUncertain => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded.GetBuffer().AsSpan(0, (int)encoded.Length));
            if (decoded is not null) CryptographicOperations.ZeroMemory(decoded.GetBuffer().AsSpan(0, (int)decoded.Length));
            ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
        }
    }

    private static async Task<bool> ReadBoundedAsync(Stream input, MemoryStream output, byte[] buffer, CancellationToken token)
    {
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > AgentBatchCodec.MaximumBytes) return false;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return true;
    }
}
