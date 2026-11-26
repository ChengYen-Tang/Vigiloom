using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Operations.ApplicationLogs;

/// <summary>Reads stored application Logs without exposing database identifiers.</summary>
public sealed class ApplicationLogHistory(IDbContextFactory<CentralLogsDbContext> contexts)
{
    public async Task<DateTimeOffset?> LastReceivedAsync(Guid serviceId, CancellationToken token)
    {
        if (serviceId.Version != 7) throw new ArgumentException("Invalid service identifier.", nameof(serviceId));
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.LogRecords.AsNoTracking().Where(row => row.ServiceId == serviceId)
            .MaxAsync(row => (DateTimeOffset?)row.ReceivedAt, token);
    }
    public async Task<TelemetryPage<TelemetryLog, TelemetryContext>> GetAsync(Guid serviceId, DateTimeOffset from,
        DateTimeOffset to, int page, string? traceId, CancellationToken token)
    {
        byte[]? trace = null;
        if (traceId is not null)
        {
            try { trace = Convert.FromHexString(traceId); }
            catch (FormatException) { throw new ArgumentException("Invalid trace identifier."); }
        }
        if (serviceId.Version != 7 || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || to <= from ||
            to - from > TimeSpan.FromDays(31) || page is < 1 or > 100 ||
            trace is not null && (trace.Length != 16 || trace.AsSpan().IndexOfAnyExcept((byte)0) < 0))
            throw new ArgumentException("Invalid application Log query.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var query = db.LogRecords.AsNoTrackingWithIdentityResolution().Include(x => x.Context)
            .Where(x => x.ServiceId == serviceId && x.OccurredAt >= from && x.OccurredAt < to);
        if (trace is not null) query = query.Where(x => x.TraceId == trace);
        var rows = await query.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((page - 1) * 25).Take(26).ToArrayAsync(token);
        var indexes = new Dictionary<long, int>();
        var shared = new List<TelemetryContext>();
        var items = new List<TelemetryLog>(Math.Min(rows.Length, 25));
        foreach (var row in rows.Take(25))
        {
            if (!indexes.TryGetValue(row.ContextId, out var index))
            {
                index = shared.Count; indexes.Add(row.ContextId, index);
                shared.Add(new(row.Context.ResourcePayload, row.Context.ScopePayload, row.Context.ResourceSchemaUrl, row.Context.ScopeSchemaUrl));
            }
            items.Add(new(row.InstanceId, row.OccurredAt, row.ReceivedAt, row.UsedReceivedTime, row.UsedObservedTime,
                row.TraceId is null ? null : Convert.ToHexStringLower(row.TraceId),
                row.SpanId is null ? null : Convert.ToHexStringLower(row.SpanId), index, row.RecordPayload));
        }
        return new(items, shared, rows.Length > 25 && page < 100, rows.Length > 25 && page == 100);
    }
}
