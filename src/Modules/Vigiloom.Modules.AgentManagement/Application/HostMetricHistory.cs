using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads a bounded, stable page across the device's registered Agent identities.</summary>
public sealed class HostMetricHistory(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices)
{
    public const int PageSize = 200;
    private static readonly TimeSpan MaximumRange = TimeSpan.FromDays(31);
    private static readonly TimeSpan MaximumAggregateRange = TimeSpan.FromDays(366);

    /// <summary>Aggregates one series in UTC buckets. Missing buckets are omitted.</summary>
    public async Task<HostMetricAggregate?> AggregateAsync(Guid deviceId, Guid agentId, DateTimeOffset from,
        DateTimeOffset until, AgentMetricKind kind, string source, string? dimension, int bucketSeconds, CancellationToken token)
    {
        from = from.ToUniversalTime();
        until = until.ToUniversalTime();
        if (deviceId == Guid.Empty || agentId == Guid.Empty || from.Year < 2 || until <= from || until - from > MaximumAggregateRange ||
            !Enum.IsDefined(kind) || kind == AgentMetricKind.CollectionUnavailable ||
            string.IsNullOrWhiteSpace(source) || source.Length > 128 || source.Any(char.IsControl) ||
            dimension is { Length: > 256 } || dimension?.Any(char.IsControl) == true ||
            bucketSeconds is < 1 or > 86400)
            throw new ArgumentException("Invalid metric aggregate query.");
        // Align to Unix epoch buckets so overlapping queries use the same labels.
        var unixSeconds = from.ToUnixTimeSeconds();
        var offset = (unixSeconds % bucketSeconds + bucketSeconds) % bucketSeconds;
        var anchor = DateTimeOffset.FromUnixTimeSeconds(unixSeconds - offset);
        if ((until.AddTicks(-1).ToUnixTimeSeconds() - anchor.ToUnixTimeSeconds()) / bucketSeconds + 1 > 1000)
            throw new ArgumentException("The metric aggregate contains too many buckets.");
        if (await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await db.Agents.AsNoTracking().AnyAsync(x => x.Id == agentId && x.DeviceId == deviceId, token)) return null;
        return await HostMetricRollupQuery.AggregateAsync(db, agentId, from, until, kind, source, dimension, bucketSeconds, anchor, token);
    }

    /// <summary>Finds metric series in both retained samples and five-minute summaries.</summary>
    public async Task<HostMetricSeriesPage?> GetSeriesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until,
        AgentMetricKind kind, int page, CancellationToken token)
    {
        from = from.ToUniversalTime();
        until = until.ToUniversalTime();
        if (deviceId == Guid.Empty || from.Year < 2 || until <= from || until - from > MaximumAggregateRange ||
            !Enum.IsDefined(kind) || kind == AgentMetricKind.CollectionUnavailable || page < 0 || page > int.MaxValue / PageSize)
            throw new ArgumentException("Invalid metric series query.");
        if (await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var ids = db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Id);
        var startWindow = new DateTimeOffset(from.Ticks - from.Ticks % TimeSpan.FromMinutes(5).Ticks, TimeSpan.Zero);
        var raw = db.HostMetrics.AsNoTracking().Where(x => ids.Contains(x.AgentId) && x.Kind == kind && x.OccurredAt >= from && x.OccurredAt < until)
            .Select(x => new { x.AgentId, Source = EF.Functions.Collate(x.Source, "Latin1_General_100_BIN2"), SourceLength = EF.Functions.DataLength(x.Source),
                Dimension = x.Dimension == null ? null : EF.Functions.Collate(x.Dimension, "Latin1_General_100_BIN2"), DimensionLength = EF.Functions.DataLength(x.Dimension) });
        var summaries = db.HostMetricRollups.AsNoTracking().Where(x => ids.Contains(x.AgentId) && x.Kind == kind && x.WindowStart >= startWindow && x.WindowStart < until)
            .Select(x => new { x.AgentId, Source = EF.Functions.Collate(x.Source, "Latin1_General_100_BIN2"), SourceLength = EF.Functions.DataLength(x.Source),
                Dimension = x.Dimension == null ? null : EF.Functions.Collate(x.Dimension, "Latin1_General_100_BIN2"), DimensionLength = EF.Functions.DataLength(x.Dimension) });
        var rows = await raw.Union(summaries).OrderBy(x => x.AgentId).ThenBy(x => x.Source).ThenBy(x => x.SourceLength)
            .ThenBy(x => x.Dimension).ThenBy(x => x.DimensionLength).Skip(page * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        return new(rows.Take(PageSize).Select(x => new HostMetricSeries(x.AgentId, x.Source, x.Dimension)).ToArray(), rows.Length > PageSize);
    }

    public async Task<HostMetricPage?> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, AgentMetricKind? kind, CancellationToken token)
    {
        from = from.ToUniversalTime();
        until = until.ToUniversalTime();
        if (deviceId == Guid.Empty || until <= from || until - from > MaximumRange || kind is { } filter && !Enum.IsDefined(filter) ||
            !TryReadCursor(cursor, from, until, out var position)) throw new ArgumentException("Invalid metric time range or cursor.");
        if (await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var ids = db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Id);
        var query = db.HostMetrics.AsNoTracking().Where(x => ids.Contains(x.AgentId) && x.OccurredAt >= from && x.OccurredAt < until);
        if (kind is { } metricKind) query = query.Where(x => x.Kind == metricKind);
        if (position is { } last)
            query = query.Where(x => x.OccurredAt < last.Time || x.OccurredAt == last.Time && x.RowId < last.RowId);
        var rows = await query.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Select(x => new { x.RowId, x.AgentId, x.OccurredAt, x.ReceivedAt, x.Kind, x.Source, x.Dimension, x.Value })
            .Take(PageSize + 1).ToArrayAsync(token);
        var count = Math.Min(rows.Length, PageSize);
        var values = new HostMetricValue[count];
        for (var i = 0; i < count; i++)
        {
            var row = rows[i];
            values[i] = new(row.OccurredAt, row.ReceivedAt, row.Kind, row.Source, row.Dimension, row.Value) { AgentId = row.AgentId };
        }
        var next = rows.Length > PageSize ? WriteCursor(rows[count - 1].OccurredAt, rows[count - 1].RowId) : null;
        return new(values, next);
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
