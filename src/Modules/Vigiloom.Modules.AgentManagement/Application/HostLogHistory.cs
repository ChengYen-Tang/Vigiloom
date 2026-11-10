using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads bounded pages of private host observations by central receipt time.</summary>
public sealed class HostLogHistory(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices)
{
    public const int PageSize = 25;
    private static readonly TimeSpan MaximumRange = TimeSpan.FromDays(31);

    public async Task<HostLogPage?> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until,
        string? cursor, Guid? sourceId, CancellationToken token)
    {
        from = from.ToUniversalTime(); until = until.ToUniversalTime();
        if (deviceId == Guid.Empty || until <= from || until - from > MaximumRange ||
            sourceId is { Version: not 7 } || !TryReadCursor(cursor, from, until, out var position))
            throw new ArgumentException("Invalid host log time range, source or cursor.");
        if (await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var agentIds = db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Id);
        var query = db.HostLogs.AsNoTracking().Where(x => agentIds.Contains(x.AgentId) && x.ReceivedAt >= from && x.ReceivedAt < until);
        if (sourceId is { } source) query = query.Where(x => x.SourceId == source);
        if (position is { } last) query = query.Where(x => x.ReceivedAt < last.Time || x.ReceivedAt == last.Time && x.RowId < last.RowId);
        var rows = await query.OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RowId)
            .Select(x => new { x.RowId, x.AgentId, x.MessageId, x.SourceId, x.SourceKind, x.SourceName, x.ParserVersion,
                x.OccurredAt, x.ReceivedAt, x.Content, x.Provider, x.EventId, x.Level, x.User, x.Process, x.GapReason, x.GapAffectedCount })
            .Take(PageSize + 1).ToArrayAsync(token);
        var count = Math.Min(rows.Length, PageSize);
        var items = new HostLogEntry[count];
        for (var index = 0; index < count; index++)
        {
            var row = rows[index];
            items[index] = new(row.AgentId, row.MessageId, row.SourceId, row.SourceKind, row.SourceName, row.ParserVersion,
                row.OccurredAt, row.ReceivedAt, row.Content, row.Provider, row.EventId, row.Level, row.User, row.Process,
                row.GapReason is { } reason ? new(reason, row.GapAffectedCount) : null);
        }
        return new(items, rows.Length > PageSize ? WriteCursor(rows[count - 1].ReceivedAt, rows[count - 1].RowId) : null);
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
