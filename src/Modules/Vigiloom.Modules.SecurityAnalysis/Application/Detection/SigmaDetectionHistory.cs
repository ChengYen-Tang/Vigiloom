using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Reads private, bounded device detection evidence without exposing storage row IDs.</summary>
public sealed class SigmaDetectionHistory(IDbContextFactory<SecurityAnalysisDbContext> contexts, IDeviceAgentSource devices)
{
    private const int PageSize = 50;

    public async Task<SigmaDetectionEventPage?> GetEventsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until,
        string? cursor, CancellationToken token)
    {
        var position = Validate(deviceId, ref from, ref until, cursor);
        if (await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var query = db.SigmaDetectionResults.AsNoTracking().Where(x => x.DeviceId == deviceId && x.ReceivedAt >= from && x.ReceivedAt < until);
        if (position is { } last) query = query.Where(x => x.ReceivedAt < last.Time || x.ReceivedAt == last.Time && x.RowId < last.RowId);
        var rows = await (from result in query
            join test in db.SigmaRuleTests.AsNoTracking() on result.TestId equals test.Id
            orderby result.ReceivedAt descending, result.RowId descending
            select new { result.RowId, result.AgentId, result.MessageId, test.SourceId, test.RevisionId, result.TestId, test.MatcherVersion, test.Path, test.Digest,
                result.ActivationVersion, result.Result, result.OccurredAt, result.ReceivedAt, result.EvaluatedAt,
                result.CorrelationCount, result.CorrelationFirstAt, result.CorrelationLastAt }).Take(PageSize + 1).ToArrayAsync(token);
        var items = rows.Take(PageSize).Select(x => new SigmaDetectionEvent(x.AgentId, x.MessageId, x.SourceId, x.RevisionId,
            x.TestId, x.MatcherVersion, Convert.ToBase64String(x.ActivationVersion), x.Path, x.Digest, (SigmaExpectedOutcome)x.Result, x.OccurredAt, x.ReceivedAt, x.EvaluatedAt,
            x.CorrelationCount is { } count && x.CorrelationFirstAt is { } first && x.CorrelationLastAt is { } last ? new(count, first, last) : null)).ToArray();
        return new(items, rows.Length > PageSize ? WriteCursor(rows[PageSize - 1].ReceivedAt, rows[PageSize - 1].RowId) : null);
    }

    public async Task<SigmaDetectionGapPage?> GetGapsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until,
        string? cursor, CancellationToken token)
    {
        var position = Validate(deviceId, ref from, ref until, cursor);
        if (await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var query = db.SigmaEventGaps.AsNoTracking().Where(x => x.DeviceId == deviceId && x.RecordedAt >= from && x.RecordedAt < until);
        if (position is { } last) query = query.Where(x => x.RecordedAt < last.Time || x.RecordedAt == last.Time && x.RowId < last.RowId);
        var rows = await query.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.RowId).Take(PageSize + 1)
            .Select(x => new { x.RowId, x.AgentId, x.MessageId, x.Kind, x.RecordedAt }).ToArrayAsync(token);
        var items = rows.Take(PageSize).Select(x => new SigmaDetectionGap(x.AgentId, x.MessageId,
            (SigmaDetectionGapKind)x.Kind, x.RecordedAt)).ToArray();
        return new(items, rows.Length > PageSize ? WriteCursor(rows[PageSize - 1].RecordedAt, rows[PageSize - 1].RowId) : null);
    }

    private static (DateTimeOffset Time, long RowId)? Validate(Guid deviceId, ref DateTimeOffset from, ref DateTimeOffset until, string? cursor)
    {
        from = from.ToUniversalTime(); until = until.ToUniversalTime();
        if (deviceId.Version != 7 || until <= from || until - from > TimeSpan.FromDays(31)) throw new ArgumentException("Invalid detection query.");
        if (cursor is null) return null;
        Span<byte> bytes = stackalloc byte[16];
        if (cursor.Length != 24 || !Convert.TryFromBase64String(cursor, bytes, out var length) || length != bytes.Length)
            throw new ArgumentException("Invalid detection cursor.");
        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        var rowId = BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || rowId <= 0)
            throw new ArgumentException("Invalid detection cursor.");
        var time = new DateTimeOffset(ticks, TimeSpan.Zero);
        if (time < from || time >= until) throw new ArgumentException("Invalid detection cursor.");
        return (time, rowId);
    }

    private static string WriteCursor(DateTimeOffset time, long rowId)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, time.UtcTicks);
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], rowId);
        return Convert.ToBase64String(bytes);
    }
}
