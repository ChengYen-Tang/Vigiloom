using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Bounded internal history query; callers are responsible for read authorization.</summary>
internal sealed class LogHistory(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    internal const int PageSize = 25;
    internal const int MaximumPage = 100;
    internal sealed record Page(IReadOnlyList<LogRecord> Items, bool HasMore, bool Truncated);

    internal async Task<Page> QueryAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to, int page, CancellationToken token, byte[]? traceId = null)
    {
        if (serviceId.Version != 7 || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero ||
            to <= from || to - from > TimeSpan.FromDays(31) || page is < 1 or > MaximumPage ||
            traceId is not null && (traceId.Length != 16 || traceId.AsSpan().IndexOfAnyExcept((byte)0) < 0))
            throw new ArgumentException("A service, UTC range up to 31 days, and page from 1 to 100 are required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var query = db.LogRecords.AsNoTrackingWithIdentityResolution().Include(x => x.Context)
            .Where(x => x.ServiceId == serviceId && x.OccurredAt >= from && x.OccurredAt < to);
        if (traceId is not null) query = query.Where(x => x.TraceId == traceId);
        var entries = await query
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        return new(entries.Take(PageSize).ToArray(), entries.Length > PageSize && page < MaximumPage,
            entries.Length > PageSize && page == MaximumPage);
    }
}
