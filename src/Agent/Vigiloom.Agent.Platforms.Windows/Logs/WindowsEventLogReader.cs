using System.Runtime.Versioning;
using System.ComponentModel;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Platforms.Windows.Logs;

[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogReader(string channel) : ILogSourceReader
{
    public ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token)
    {
        EventLogReadResult result;
        try { result = EventLogSource.ReadNext(channel, NativeLogCursor.Decode(cursor), token); }
        catch (Win32Exception exception) { throw new IOException("Windows event source is unavailable.", exception); }
        return ValueTask.FromResult(result.Status switch
        {
            EventLogReadStatus.End => new LogReadResult(LogReadStatus.End),
            EventLogReadStatus.CursorMissing => new LogReadResult(LogReadStatus.Gap, NativeLogCursor.Restart,
                Gap: new(AgentLogGapReason.CursorLost, null)),
            EventLogReadStatus.Oversized => new LogReadResult(LogReadStatus.Gap, NativeLogCursor.Encode(result.NextCursor!),
                Gap: new(AgentLogGapReason.Oversized, 1)),
            EventLogReadStatus.Record when result.Observation is { } item => new LogReadResult(LogReadStatus.Record,
                NativeLogCursor.Encode(result.NextCursor!), new(item.OccurredAt, item.Xml, item.Provider, item.EventId,
                    item.Level, item.User, item.Process)),
            _ => throw new InvalidDataException("Invalid Windows event observation.")
        });
    }
}
