using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Reads a bounded page of historical changes across a device's Agent registrations.</summary>
public sealed class InventoryChangeQuery(IDbContextFactory<SecurityAnalysisDbContext> contexts)
{
    private const int PageSize = 50;
    private const int PageCharacterBudget = 2 * 1024 * 1024;

    public async Task<InventoryChangePage> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until,
        InventorySource? source, string? cursor, CancellationToken token)
    {
        from = from.ToUniversalTime();
        until = until.ToUniversalTime();
        if (deviceId.Version != 7 || until <= from || until - from > TimeSpan.FromDays(31) ||
            source is { } value && !Enum.IsDefined(value) || !TryReadCursor(cursor, from, until, out var position))
            throw new ArgumentException("Invalid inventory change query.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var query = db.InventoryChanges.AsNoTracking().Where(x => x.DeviceId == deviceId && x.ObservedAt >= from && x.ObservedAt < until);
        if (source is { } filter) query = query.Where(x => x.Source == filter);
        if (position is { } last) query = query.Where(x => x.ObservedAt < last.Time || x.ObservedAt == last.Time && x.RowId < last.RowId);
        var rows = query.OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.RowId)
            .Select(x => new { x.RowId, x.AgentId, x.Source, x.ObservedAt, x.Kind, x.BeforeJson, x.AfterJson })
            .Take(PageSize + 1).AsAsyncEnumerable();
        var values = new List<InventoryChangeView>(PageSize);
        DateTimeOffset lastTime = default;
        long lastRow = 0;
        var characters = 0;
        var more = false;
        await foreach (var row in rows.WithCancellation(token))
        {
            var size = (row.BeforeJson?.Length ?? 0) + (row.AfterJson?.Length ?? 0);
            if (values.Count == PageSize || values.Count > 0 && size > PageCharacterBudget - characters) { more = true; break; }
            var before = row.BeforeJson is null ? null : JsonSerializer.Deserialize<InventoryEntry>(row.BeforeJson);
            var after = row.AfterJson is null ? null : JsonSerializer.Deserialize<InventoryEntry>(row.AfterJson);
            if (row.BeforeJson is not null && before is null || row.AfterJson is not null && after is null ||
                !Enum.IsDefined((InventoryChangeType)row.Kind)) throw new InvalidDataException("Saved inventory change is invalid.");
            values.Add(new(row.AgentId, row.Source, row.ObservedAt, (InventoryChangeType)row.Kind, before, after));
            characters += size;
            lastTime = row.ObservedAt;
            lastRow = row.RowId;
        }
        return new(values, more ? WriteCursor(lastTime, lastRow) : null);
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
        var row = BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
        if (ticks < from.UtcTicks || ticks >= until.UtcTicks || row <= 0) return false;
        position = (new DateTimeOffset(ticks, TimeSpan.Zero), row);
        return true;
    }
}
