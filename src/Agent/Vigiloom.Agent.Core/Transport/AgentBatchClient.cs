using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Transport;

/// <summary>Sends an already persisted batch; only a confirmed central commit removes it locally.</summary>
public sealed class AgentBatchClient(HttpClient http, TimeProvider clock)
{
    public async Task<BatchSendOutcome> SendNextAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? BatchSendResult.ServerMismatch : BatchSendResult.NotRegistered);
        var id = identity.AgentId!.Value;
        var queue = new DurableBatchQueue(vault, id);
        var pending = await queue.PeekAsync(token);
        if (pending is null) return new(BatchSendResult.NoPendingBatch);
        if (!AgentBatchCodec.TryRead(pending.Payload, out var batch) || batch!.BatchId != pending.BatchId ||
            batch.EnqueuedAt != pending.EnqueuedAt || batch.Items.Count != pending.RecordCount)
        {
            await queue.QuarantineAsync(pending.BatchId, QueueRejection.InvalidLocalBatch, token);
            return new(BatchSendResult.InvalidLocalBatch);
        }
        var credential = $"vla_{id:D}.{identity.MachineSecret}";
        using var compressed = await CompressIfUsefulAsync(pending.Payload, token);
        using var first = await SendAsync(expected, credential, pending.Payload, compressed, token);
        // Older platforms explicitly reject Content-Encoding. Retry the same stored bytes
        // uncompressed instead of treating their 415 as a permanent batch rejection.
        using var fallback = compressed is not null && first.StatusCode == HttpStatusCode.UnsupportedMediaType
            ? await SendAsync(expected, credential, pending.Payload, null, token) : null;
        var response = fallback ?? first;
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            await queue.AcknowledgeAsync(pending.BatchId, token);
            return new(BatchSendResult.Delivered);
        }
        var status = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => BatchSendResult.Unauthorized,
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType => BatchSendResult.Rejected,
            _ => BatchSendResult.Retry
        };
        if (status == BatchSendResult.Rejected)
        {
            var reason = response.StatusCode switch
            {
                HttpStatusCode.BadRequest => QueueRejection.BadRequest,
                HttpStatusCode.Conflict => QueueRejection.Conflict,
                HttpStatusCode.RequestEntityTooLarge => QueueRejection.PayloadTooLarge,
                HttpStatusCode.UnsupportedMediaType => QueueRejection.UnsupportedMediaType,
                _ => throw new InvalidOperationException("Unexpected permanent batch response.")
            };
            await queue.QuarantineAsync(pending.BatchId, reason, token);
        }
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - clock.GetUtcNow() : (TimeSpan?)null);
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return new(status, delay);
    }

    private static async Task<MemoryStream?> CompressIfUsefulAsync(byte[] payload, CancellationToken token)
    {
        if (payload.Length < 1024) return null;
        var compressed = new MemoryStream();
        try
        {
            await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                await gzip.WriteAsync(payload, token);
            if (compressed.Length >= payload.Length) { compressed.Dispose(); return null; }
            return compressed;
        }
        catch { compressed.Dispose(); throw; }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri server, string credential, byte[] payload, MemoryStream? compressed, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/v1/agent/batches"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Content = compressed is null ? new ByteArrayContent(payload) :
            new ByteArrayContent(compressed.GetBuffer(), 0, checked((int)compressed.Length));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (compressed is not null) request.Content.Headers.ContentEncoding.Add("gzip");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
}

public enum BatchSendResult { NoPendingBatch, Delivered, NotRegistered, ServerMismatch, Unauthorized, Retry, Rejected, InvalidLocalBatch }
public readonly record struct BatchSendOutcome(BatchSendResult Status, TimeSpan? RetryAfter = null);
