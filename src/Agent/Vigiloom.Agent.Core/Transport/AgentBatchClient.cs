using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;

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
        if (!MatchesQueueEntry(pending, out var schemaVersion))
        {
            await queue.QuarantineAsync(pending.BatchId, QueueRejection.InvalidLocalBatch, token);
            return new(BatchSendResult.InvalidLocalBatch);
        }
        var credential = $"vla_{id:D}.{identity.MachineSecret}";
        using var compressed = await CompressIfUsefulAsync(pending.Payload, token);
        using var first = await SendAsync(expected, credential, pending.Payload, compressed, token);
        // Older platforms explicitly reject Content-Encoding. Retry the same stored bytes
        // uncompressed instead of treating their 415 as a permanent batch rejection.
        HttpResponseMessage? fallbackResponse = null;
        if (compressed is not null && first.StatusCode == HttpStatusCode.UnsupportedMediaType)
        {
            token.ThrowIfCancellationRequested();
            fallbackResponse = await SendAsync(expected, credential, pending.Payload, null, token);
        }
        using var fallback = fallbackResponse;
        var response = fallback ?? first;
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            await queue.AcknowledgeAsync(pending.BatchId, token);
            return new(BatchSendResult.Delivered);
        }
        // Older servers can reject a newer valid schema with 400. Preserve its bytes
        // until the server explicitly advertises that schema.
        var unknownSchema = schemaVersion > 1 && response.StatusCode == HttpStatusCode.BadRequest && !SupportsSchema(response, schemaVersion);
        var status = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => BatchSendResult.Unauthorized,
            HttpStatusCode.BadRequest when unknownSchema => BatchSendResult.Retry,
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Gone or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType => BatchSendResult.Rejected,
            _ => BatchSendResult.Retry
        };
        if (status == BatchSendResult.Rejected)
        {
            var reason = response.StatusCode switch
            {
                HttpStatusCode.BadRequest => QueueRejection.BadRequest,
                HttpStatusCode.Conflict => QueueRejection.Conflict,
                HttpStatusCode.Gone => QueueRejection.Expired,
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

    private static bool SupportsSchema(HttpResponseMessage response, int schemaVersion) =>
        response.Headers.TryGetValues(AgentMetricBatchCodec.SupportedSchemaHeader, out var values) &&
        values.SelectMany(value => value.Split(',')).Any(value => string.Equals(value.Trim(), schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

    private static bool MatchesQueueEntry(QueuedBatch pending, out int schemaVersion)
    {
        schemaVersion = 1;
        if (AgentBatchCodec.TryRead(pending.Payload, out var runtime))
            return runtime!.BatchId == pending.BatchId && runtime.EnqueuedAt == pending.EnqueuedAt && runtime.Items.Count == pending.RecordCount;
        if (AgentMetricBatchCodec.TryRead(pending.Payload, out var metrics))
        {
            schemaVersion = AgentMetricBatchCodec.SchemaVersion;
            return metrics!.BatchId == pending.BatchId && metrics.EnqueuedAt == pending.EnqueuedAt && metrics.Items.Count == pending.RecordCount;
        }
        if (AgentLogBatchCodec.TryRead(pending.Payload, out var logs))
        {
            schemaVersion = AgentLogBatchCodec.SchemaVersion;
            return logs!.BatchId == pending.BatchId && logs.EnqueuedAt == pending.EnqueuedAt && logs.Items.Count == pending.RecordCount;
        }
        if (InventoryChunkCodec.TryRead(pending.Payload, out var inventory))
        {
            schemaVersion = InventoryChunkCodec.SchemaVersion;
            // A completed empty source still occupies one bounded queue record.
            return inventory!.BatchId == pending.BatchId && inventory.EnqueuedAt == pending.EnqueuedAt &&
                Math.Max(1, inventory.Items.Count) == pending.RecordCount;
        }
        return false;
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
