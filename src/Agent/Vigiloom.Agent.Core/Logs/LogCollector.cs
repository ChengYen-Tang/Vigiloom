using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Logs;

public enum LogReadStatus { End, Record, Gap }
public sealed record LogObservation(DateTimeOffset? OccurredAt, string Content, string? Provider = null,
    long? EventId = null, string? Level = null, string? User = null, string? Process = null);
public sealed record LogReadResult(LogReadStatus Status, string? NextCursor = null, LogObservation? Record = null, AgentLogGap? Gap = null);

/// <summary>A source returns a candidate cursor only; the collector commits it with the corresponding batch.</summary>
public interface ILogSourceReader
{
    ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token);
}

public sealed record LogSource(Guid Id, Guid Generation, AgentLogSourceKind Kind, string Name, int ParserVersion);
public enum LogCollectStatus { End, Queued, Full, ExceedsCapacity, CursorConflict, BatchConflict, OwnerChanged, SettingsChanged }
public sealed record LogCollectBatchResult(LogCollectStatus Status, int Count, bool MayHaveMore);

/// <summary>Transfers an observation or gap into the durable queue before advancing its source cursor.</summary>
public sealed class LogCollector(DurableBatchQueue queue, TimeProvider clock)
{
    /// <summary>Queues a bounded run from one source and advances only the cursor covered by that batch.</summary>
    public async Task<LogCollectBatchResult> CollectBatchAsync(AgentSettingsOwner owner, LogSource source,
        ILogSourceReader reader, int maximumItems, Func<bool> stillCurrent, Guid? settingsSaveToken, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(stillCurrent);
        if (maximumItems is < 1 or > AgentLogBatchCodec.MaximumItems) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        if (!stillCurrent()) return new(LogCollectStatus.SettingsChanged, 0, false);
        var current = await queue.ReadLogCursorAsync(owner, source.Id, token);
        if (current is null || current.Generation != source.Generation) return new(LogCollectStatus.CursorConflict, 0, false);
        var position = current.Position;
        var items = new List<AgentLogRecord>(maximumItems);
        var now = clock.GetUtcNow();
        // JSON escaping uses at most six ASCII bytes per UTF-16 code unit.
        // Reserve envelope and per-record overhead to stay below the transport limit.
        long estimatedBytes = 8192 + 6L * source.Name.Length;
        var mayHaveMore = false;
        while (items.Count < maximumItems)
        {
            if (!stillCurrent()) return new(LogCollectStatus.SettingsChanged, 0, false);
            var result = await reader.ReadAsync(position, token);
            if (!stillCurrent()) return new(LogCollectStatus.SettingsChanged, 0, false);
            if (result.Status == LogReadStatus.End)
            {
                if (result.NextCursor is not null || result.Record is not null || result.Gap is not null)
                    throw new InvalidDataException("An end-of-source result cannot advance the cursor or contain data.");
                break;
            }
            if (string.IsNullOrEmpty(result.NextCursor) || string.Equals(position, result.NextCursor, StringComparison.Ordinal) ||
                (result.Status == LogReadStatus.Record && (result.Record is null || result.Record.Content is null || result.Gap is not null)) ||
                (result.Status == LogReadStatus.Gap && (result.Gap is null || result.Record is not null)) ||
                result.Status is not (LogReadStatus.Record or LogReadStatus.Gap)) throw new InvalidDataException("Invalid source observation.");
            var observation = result.Record;
            var size = 512L + 6L * ((observation?.Content.Length ?? 0) + (observation?.Provider?.Length ?? 0) +
                (observation?.Level?.Length ?? 0) + (observation?.User?.Length ?? 0) + (observation?.Process?.Length ?? 0));
            if (items.Count != 0 && estimatedBytes + size > AgentBatchCodec.MaximumBytes) { mayHaveMore = true; break; }
            estimatedBytes += size;
            items.Add(new AgentLogRecord(Guid.CreateVersion7(), observation is null ? now : observation.OccurredAt, observation?.Content ?? "",
                observation?.Provider, observation?.EventId, observation?.Level, observation?.User, observation?.Process, result.Gap));
            position = result.NextCursor;
        }
        if (items.Count == 0) return new(LogCollectStatus.End, 0, false);
        mayHaveMore |= items.Count == maximumItems;
        var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, source.Id,
            source.Kind, source.Name, source.ParserVersion, items);
        var payload = AgentLogBatchCodec.Serialize(batch);
        var queued = new QueuedBatch(batch.BatchId, now, items.Count, payload);
        var status = await queue.EnqueueLogAsync(owner, source.Id, source.Generation, current.Position, position!, queued, settingsSaveToken, token) switch
        {
            LogEnqueueResult.Queued or LogEnqueueResult.AlreadyCommitted => LogCollectStatus.Queued,
            LogEnqueueResult.Full => LogCollectStatus.Full,
            LogEnqueueResult.ExceedsCapacity => LogCollectStatus.ExceedsCapacity,
            LogEnqueueResult.CursorConflict => LogCollectStatus.CursorConflict,
            LogEnqueueResult.BatchConflict => LogCollectStatus.BatchConflict,
            LogEnqueueResult.OwnerChanged => LogCollectStatus.OwnerChanged,
            LogEnqueueResult.SettingsChanged => LogCollectStatus.SettingsChanged,
            _ => throw new InvalidOperationException("Unknown queue result.")
        };
        return new(status, status == LogCollectStatus.Queued ? items.Count : 0, status == LogCollectStatus.Queued && mayHaveMore);
    }

    public async Task<LogCollectStatus> CollectNextAsync(AgentSettingsOwner owner, LogSource source,
        ILogSourceReader reader, CancellationToken token)
        => await CollectNextAsync(owner, source, reader, static () => true, token);

    public async Task<LogCollectStatus> CollectNextAsync(AgentSettingsOwner owner, LogSource source,
        ILogSourceReader reader, Func<bool> stillCurrent, CancellationToken token, Guid? settingsSaveToken = null)
        => (await CollectBatchAsync(owner, source, reader, 1, stillCurrent, settingsSaveToken, token)).Status;
}
