using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

/// <summary>Updates one bucket inside the caller's sample transaction.</summary>
internal static class PingRollupWriter
{
    internal static DateTimeOffset WindowStart(DateTimeOffset time)
    {
        var ticks = time.ToUniversalTime().Ticks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.FromMinutes(5).Ticks, TimeSpan.Zero);
    }

    internal static async Task AddAsync(NetworkDbContext db, PingSample sample, CancellationToken token)
    {
        var start = WindowStart(sample.OccurredAt);
        var target = sample.Target;
        // The serializable key-range lock also covers a bucket that has not been inserted yet.
        var bucket = await db.PingRollups.FromSqlInterpolated($"""
            SELECT * FROM [network].[PingRollups] WITH (UPDLOCK, HOLDLOCK)
            WHERE [DeviceId] = {sample.DeviceId} AND [WindowStart] = {start}
              AND ([Target] = {target} OR ([Target] IS NULL AND {target} IS NULL)) AND [Location] = {sample.Location}
            """).SingleOrDefaultAsync(token);
        if (bucket is null)
        {
            bucket = new PingRollup(sample.DeviceId, target, sample.Location, start);
            db.PingRollups.Add(bucket);
        }
        bucket.Add(sample);
        sample.MarkAggregated();
    }
}
