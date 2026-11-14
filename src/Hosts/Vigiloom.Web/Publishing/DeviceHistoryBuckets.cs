using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Reads elapsed central Ping buckets without projecting target or host details.</summary>
internal static class DeviceHistoryBuckets
{
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);

    internal static async Task<PublicDeviceHistory> ReadAsync(IDbContextFactory<NetworkDbContext> contexts, Guid id, string target,
        DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (from >= to) return new([]);
        var remainder = from.UtcTicks % Bucket.Ticks;
        if (remainder != 0) from = from.AddTicks(Bucket.Ticks - remainder);
        to = to.AddTicks(-(to.UtcTicks % Bucket.Ticks));
        if (from >= to) return new([]);
        await using var db = await contexts.CreateDbContextAsync(token);
        var points = await db.PingRollups.AsNoTracking()
            .Where(x => x.DeviceId == id && x.Target == target && x.Location == "central" && x.WindowStart >= from && x.WindowStart < to)
            .OrderBy(x => x.WindowStart)
            .Select(x => new PublicDeviceHistoryPoint(x.WindowStart, x.Successes == 0 || x.LatencyOverflow ? null : x.LatencySum / x.Successes))
            .ToArrayAsync(token);
        return new(points);
    }
}
