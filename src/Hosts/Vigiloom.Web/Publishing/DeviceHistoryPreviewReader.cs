using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Administrator preview of elapsed central Ping buckets after the current publication began.</summary>
internal sealed class DeviceHistoryPreviewReader(IDbContextFactory<ResourcesDbContext> resources, IDbContextFactory<NetworkDbContext> network)
{
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);

    public async Task<PublicDeviceHistory?> ReadAsync(Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        await using var db = await resources.CreateDbContextAsync(token);
        var before = await SnapshotAsync(db, id, token);
        if (before is null || before.Since == Device.PendingPublicSince || !PublicationPolicy.TryValidateName(before.Name, out _)) return null;
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var start = before.Since > now.AddDays(-7) ? before.Since : now.AddDays(-7);
        var remainder = start.UtcTicks % Bucket.Ticks;
        if (remainder != 0) start = start.AddTicks(Bucket.Ticks - remainder);
        var end = now.AddTicks(-(now.UtcTicks % Bucket.Ticks));
        await using var networkDb = await network.CreateDbContextAsync(token);
        var points = await networkDb.PingRollups.AsNoTracking()
            .Where(x => x.DeviceId == id && x.Target == before.Target && x.Location == "central" && x.WindowStart >= start && x.WindowStart < end)
            .OrderBy(x => x.WindowStart)
            .Select(x => new PublicDeviceHistoryPoint(x.WindowStart, x.Successes == 0 || x.LatencyOverflow ? null : x.LatencySum / x.Successes))
            .ToArrayAsync(token);
        var after = await SnapshotAsync(db, id, token);
        return after is not null && before.Since == after.Since && before.Version.AsSpan().SequenceEqual(after.Version)
            ? new(points) : null;
    }

    private static Task<Snapshot?> SnapshotAsync(ResourcesDbContext db, Guid id, CancellationToken token) =>
        db.Devices.AsNoTracking().Where(x => x.Id == id && x.IsPublic && !x.IsArchived && x.PublicSince != null && x.PublicDisplayName != null)
            .Select(x => new Snapshot(x.PublicDisplayName!, x.Address, x.PublicSince!.Value, x.Version)).SingleOrDefaultAsync(token);

    private sealed record Snapshot(string Name, string Target, DateTimeOffset Since, byte[] Version);
}
