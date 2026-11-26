using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

internal static class HostMetricRetentionEligibility
{
    internal static IQueryable<HostMetric> Samples(DbContext db, DateTimeOffset cutoff) =>
        db.Set<HostMetric>().Where(x => x.Aggregated && x.OccurredAt < cutoff);

    internal static IQueryable<HostMetricRollup> Rollups(DbContext db, DateTimeOffset cutoff) =>
        db.Set<HostMetricRollup>().Where(r => r.WindowStart <= cutoff.AddMinutes(-5) &&
            !db.Set<HostMetric>().Any(x => !x.Aggregated && x.AgentId == r.AgentId && x.Kind == r.Kind &&
                x.OccurredAt >= r.WindowStart && x.OccurredAt < r.WindowStart.AddMinutes(5) &&
                EF.Functions.Collate(x.Source, "Latin1_General_100_BIN2") == EF.Functions.Collate(r.Source, "Latin1_General_100_BIN2") &&
                EF.Functions.DataLength(x.Source) == EF.Functions.DataLength(r.Source) &&
                (r.Dimension == null ? x.Dimension == null : x.Dimension != null &&
                    EF.Functions.Collate(x.Dimension, "Latin1_General_100_BIN2") == EF.Functions.Collate(r.Dimension, "Latin1_General_100_BIN2") &&
                    EF.Functions.DataLength(x.Dimension) == EF.Functions.DataLength(r.Dimension))));
}
