using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

public sealed record ReceptionRejectionCount(byte Kind, long Count, DateTimeOffset? FirstRecordedAt, DateTimeOffset? LastRecordedAt,
    byte? ExampleIngress, Guid? ExampleSourceId, string? ExampleSourceName);

/// <summary>Reads the bounded, best-effort receiver diagnostics; these counts are not a complete event history.</summary>
public sealed class ReceptionRejectionReader(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    public async Task<IReadOnlyList<ReceptionRejectionCount>> ListAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.ReceptionRejections.AsNoTracking().OrderBy(x => x.Kind)
            .Select(x => new ReceptionRejectionCount(x.Kind, x.ObservedCount, x.FirstRecordedAt, x.LastRecordedAt,
                x.ExampleIngress, x.ExampleSourceId, db.Sources.Where(source => source.Id == x.ExampleSourceId).Select(source => source.Name).SingleOrDefault())).ToArrayAsync(token);
        if (rows.Length != 3 || rows[0].Kind != 1 || rows[1].Kind != 2 || rows[2].Kind != 3)
            throw new InvalidOperationException("Reception rejection summaries are incomplete.");
        return rows;
    }
}
