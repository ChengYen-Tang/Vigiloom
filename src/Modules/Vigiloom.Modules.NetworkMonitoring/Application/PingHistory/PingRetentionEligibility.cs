using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

internal static class PingRetentionEligibility
{
    internal static IQueryable<PingSample> Samples(NetworkDbContext db, DateTimeOffset cutoff) =>
        db.PingSamples.Where(x => x.Aggregated && x.OccurredAt < cutoff);

    internal static IQueryable<PingRollup> Rollups(NetworkDbContext db, DateTimeOffset cutoff) =>
        db.PingRollups.Where(x => x.WindowStart <= cutoff.AddMinutes(-5) &&
            !db.PingSamples.Any(sample => !sample.Aggregated && sample.DeviceId == x.DeviceId &&
                sample.Target == x.Target && sample.Location == x.Location &&
                sample.OccurredAt >= x.WindowStart && sample.OccurredAt < x.WindowStart.AddMinutes(5)));
}
