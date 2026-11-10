using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Platforms.Linux.Logs;

[SupportedOSPlatform("linux")]
public sealed class JournalLogReader : ILogSourceReader
{
    public ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token)
    {
        JournalReadResult result;
        try { result = JournalSource.ReadNext(NativeLogCursor.Decode(cursor), token); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        { throw new IOException("System journal support is unavailable.", exception); }
        return ValueTask.FromResult(result.Status switch
        {
            JournalReadStatus.End => new LogReadResult(LogReadStatus.End),
            JournalReadStatus.CursorMissing => new LogReadResult(LogReadStatus.Gap, NativeLogCursor.Restart,
                Gap: new(AgentLogGapReason.CursorLost, null)),
            JournalReadStatus.Oversized => new LogReadResult(LogReadStatus.Gap, NativeLogCursor.Encode(result.NextCursor!),
                Gap: new(AgentLogGapReason.Oversized, 1)),
            JournalReadStatus.Record when result.Observation is not null => new LogReadResult(LogReadStatus.Record,
                NativeLogCursor.Encode(result.NextCursor!), new(result.Observation.OccurredAt, result.Observation.Content,
                    result.Observation.Provider, null, result.Observation.Level, result.Observation.User, result.Observation.Process)),
            _ => throw new InvalidDataException("Invalid journal observation.")
        });
    }
}
