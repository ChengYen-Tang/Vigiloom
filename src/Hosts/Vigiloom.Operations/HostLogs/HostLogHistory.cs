using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Operations.HostLogs;

/// <summary>Queries retained host Logs using their device association in the local timeline.</summary>
public sealed class HostLogHistory(IDbContextFactory<CentralLogsDbContext> contexts)
{
    public async Task<HostLogEvidenceView?> GetEvidenceAsync(Guid deviceId, Guid agentId, Guid messageId, CancellationToken token)
    {
        if (deviceId.Version != 7 || agentId.Version != 7 || messageId.Version != 7) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.HostLogs.FromSqlInterpolated($"""
            SELECT h.* FROM logs.HostLogs AS h WHERE EXISTS
            (SELECT 1 FROM operations.TimelineRecords AS t WHERE t.Source = 'host-log'
             AND t.LocalLogRowId = h.RowId AND t.ResourceId = {deviceId})
            """).AsNoTracking().Where(x => x.AgentId == agentId && x.MessageId == messageId)
            .Select(x => new HostLogEvidenceView(x.SourceName, x.ParserVersion, x.ReceivedAt, x.Content)).FirstOrDefaultAsync(token);
    }

    public async Task<HostLogPage> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until,
        string? cursor, Guid? sourceId, CancellationToken token)
    {
        from = from.ToUniversalTime(); until = until.ToUniversalTime();
        if (deviceId.Version != 7 || until <= from || until - from > TimeSpan.FromDays(31) ||
            sourceId is { Version: not 7 } || !TryReadCursor(cursor, from, until, out var position))
            throw new ArgumentException("Invalid host Log query.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var query = db.HostLogs.FromSqlInterpolated($"""
            SELECT h.* FROM logs.HostLogs AS h WHERE EXISTS
            (SELECT 1 FROM operations.TimelineRecords AS t WHERE t.Source = 'host-log'
             AND t.LocalLogRowId = h.RowId AND t.ResourceId = {deviceId})
            """).AsNoTracking().Where(x => x.ReceivedAt >= from && x.ReceivedAt < until);
        if (sourceId is { } source) query = query.Where(x => x.SourceId == source);
        if (position is { } last) query = query.Where(x => x.ReceivedAt < last.Time || x.ReceivedAt == last.Time && x.RowId < last.RowId);
        var rows = await query.OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RowId)
            .Take(26).ToArrayAsync(token);
        var items = rows.Take(25).Select(x => new HostLogEntry(x.AgentId, x.MessageId, x.SourceId, x.SourceKind,
            x.SourceName, x.ParserVersion, x.OccurredAt, x.ReceivedAt, x.Content, x.Provider, x.EventId,
            x.Level, x.User, x.Process, x.GapReason is { } reason ? new(reason, x.GapAffectedCount) : null)).ToArray();
        return new(items, rows.Length > 25 ? WriteCursor(rows[24].ReceivedAt, rows[24].RowId) : null);
    }

    private static string WriteCursor(DateTimeOffset time, long rowId)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, time.UtcTicks);
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], rowId);
        return Convert.ToBase64String(bytes);
    }

    private static bool TryReadCursor(string? cursor, DateTimeOffset from, DateTimeOffset until, out (DateTimeOffset Time, long RowId)? position)
    {
        position = null;
        if (cursor is null) return true;
        Span<byte> bytes = stackalloc byte[16];
        if (cursor.Length != 24 || !Convert.TryFromBase64String(cursor, bytes, out var length) || length != bytes.Length) return false;
        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        var rowId = BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || rowId <= 0) return false;
        var time = new DateTimeOffset(ticks, TimeSpan.Zero);
        if (time < from || time >= until) return false;
        position = (time, rowId);
        return true;
    }
}
