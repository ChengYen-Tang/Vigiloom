using System.Net;
using System.Net.Http.Headers;
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
        if (pending is null) return new(BatchSendResult.Empty);
        if (!AgentBatchCodec.TryRead(pending.Payload, out var batch) || batch!.BatchId != pending.BatchId ||
            batch.EnqueuedAt != pending.EnqueuedAt || batch.Items.Count != pending.RecordCount)
            return new(BatchSendResult.InvalidLocalBatch);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(expected, "api/v1/agent/batches"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{id:D}.{identity.MachineSecret}");
        request.Content = new ByteArrayContent(pending.Payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
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
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - clock.GetUtcNow() : (TimeSpan?)null);
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return new(status, delay);
    }
}

public enum BatchSendResult { Empty, Delivered, NotRegistered, ServerMismatch, Unauthorized, Retry, Rejected, InvalidLocalBatch }
public readonly record struct BatchSendOutcome(BatchSendResult Status, TimeSpan? RetryAfter = null);
