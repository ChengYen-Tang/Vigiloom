using System.ComponentModel;
using System.Security.Cryptography;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Logs;

public enum LogSourceCycleStatus { End, Queued, Full, Unsupported, Unavailable, Conflict, SettingsChanged }
public sealed record LogSourceCycleResult(Guid SourceId, LogSourceCycleStatus Status, int QueuedCount, bool MayHaveMore = false,
    DateTimeOffset? ObservedAt = null, long? Timestamp = null);

/// <summary>Runs bounded, rotating source passes. The caller owns the producer lease for this cycle's lifetime.</summary>
public sealed class LogCollectionCycle(AgentSettingsOwner owner, DurableBatchQueue queue, TimeProvider clock,
    Func<AgentLogSourceSetting, ILogSourceReader?> createReader)
{
    private const int MaximumPerSource = AgentLogBatchCodec.MaximumItems;
    private const int MaximumPerCycle = 400;
    private int nextStart;

    public async Task<IReadOnlyList<LogSourceCycleResult>> RunAsync(AgentLogSettingsSnapshot snapshot, Guid settingsSaveToken,
        Func<bool> stillCurrent, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stillCurrent);
        if (!snapshot.IsValid() || settingsSaveToken.Version != 7) throw new ArgumentException("Invalid log source snapshot or save token.", nameof(snapshot));
        var sources = snapshot.Sources.Where(source => source.Enabled).ToArray();
        if (sources.Length == 0) return [];
        var first = nextStart % sources.Length;
        nextStart = (first + 1) % sources.Length;
        var results = new List<LogSourceCycleResult>(sources.Length);
        var collector = new LogCollector(queue, clock);
        var totalQueued = 0;
        for (var index = 0; index < sources.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var setting = sources[(first + index) % sources.Length];
            if (!stillCurrent()) { results.Add(Observed(setting.SourceId, LogSourceCycleStatus.SettingsChanged, 0)); break; }
            var queued = 0;
            try
            {
                var reader = createReader(setting);
                if (reader is null) { results.Add(Observed(setting.SourceId, LogSourceCycleStatus.Unsupported, 0)); continue; }
                var cursor = await queue.ReadLogCursorAsync(owner, setting.SourceId, token);
                if (cursor?.Generation != setting.Generation &&
                    !await queue.BindLogSourceAsync(owner, setting.SourceId, cursor?.Generation, setting.Generation, false, token))
                { results.Add(Observed(setting.SourceId, LogSourceCycleStatus.Conflict, 0)); continue; }
                var source = new LogSource(setting.SourceId, setting.Generation, setting.Kind, setting.Name, setting.ParserVersion);
                var outcome = await collector.CollectBatchAsync(owner, source, reader,
                    Math.Min(Math.Min(MaximumPerSource, snapshot.MaxBatchRecords), MaximumPerCycle - totalQueued), stillCurrent, settingsSaveToken, token);
                queued = outcome.Count;
                totalQueued += queued;
                var status = outcome.Status switch
                    {
                        LogCollectStatus.End => LogSourceCycleStatus.End,
                        LogCollectStatus.Queued => LogSourceCycleStatus.Queued,
                        LogCollectStatus.Full or LogCollectStatus.ExceedsCapacity => LogSourceCycleStatus.Full,
                        LogCollectStatus.SettingsChanged => LogSourceCycleStatus.SettingsChanged,
                        _ => LogSourceCycleStatus.Conflict
                    };
                results.Add(Observed(setting.SourceId, status, queued, outcome.MayHaveMore));
                if (status is LogSourceCycleStatus.Full or LogSourceCycleStatus.SettingsChanged || totalQueued >= MaximumPerCycle) break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                UnauthorizedAccessException or Win32Exception or CryptographicException or System.Data.Common.DbException or PlatformNotSupportedException)
            {
                results.Add(Observed(setting.SourceId, LogSourceCycleStatus.Unavailable, queued));
            }
        }
        return results;
    }

    private LogSourceCycleResult Observed(Guid sourceId, LogSourceCycleStatus status, int count, bool mayHaveMore = false) =>
        new(sourceId, status, count, mayHaveMore, clock.GetUtcNow(), clock.GetTimestamp());
}
