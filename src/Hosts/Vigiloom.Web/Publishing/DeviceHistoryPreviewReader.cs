using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Administrator preview of elapsed central Ping buckets after the current publication began.</summary>
internal sealed class DeviceHistoryPreviewReader(IDbContextFactory<ResourcesDbContext> resources, IDbContextFactory<NetworkDbContext> network,
    IConfiguration? configuration = null, IServiceProvider? provider = null)
{
    public async Task<PublicDeviceHistory?> ReadAsync(Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        if (!string.IsNullOrWhiteSpace(configuration?["Services:Network:Url"]))
            return await provider!.GetRequiredService<RemoteNetworkPublicClient>().PreviewAsync(id, token);
        if (!string.IsNullOrWhiteSpace(configuration?["Services:Resources:Url"]))
        {
            var remote = provider!.GetRequiredService<RemotePublicReader>();
            var policy = await remote.DevicePolicyAsync(id, token);
            if (policy?.PublicSince is not { } since) return null;
            await using var context = await resources.CreateDbContextAsync(token);
            var currentTime = await context.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            var first = since > currentTime.AddDays(-7) ? since : currentTime.AddDays(-7);
            var preview = await DeviceHistoryBuckets.ReadAsync(network, id, policy.Target, first, currentTime, token);
            return policy == await remote.DevicePolicyAsync(id, token) ? preview : null;
        }
        await using var db = await resources.CreateDbContextAsync(token);
        var before = await SnapshotAsync(db, id, token);
        if (before is null || before.Since == Device.PendingPublicSince || !PublicationPolicy.TryValidateName(before.Name, out _)) return null;
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var start = before.Since > now.AddDays(-7) ? before.Since : now.AddDays(-7);
        var result = await DeviceHistoryBuckets.ReadAsync(network, id, before.Target, start, now, token);
        var after = await SnapshotAsync(db, id, token);
        return after is not null && before.Since == after.Since && before.Version.AsSpan().SequenceEqual(after.Version)
            ? result : null;
    }

    private static Task<Snapshot?> SnapshotAsync(ResourcesDbContext db, Guid id, CancellationToken token) =>
        db.Devices.AsNoTracking().Where(x => x.Id == id && x.IsPublic && !x.IsArchived && x.PublicSince != null && x.PublicDisplayName != null)
            .Select(x => new Snapshot(x.PublicDisplayName!, x.Address, x.PublicSince!.Value, x.Version)).SingleOrDefaultAsync(token);

    private sealed record Snapshot(string Name, string Target, DateTimeOffset Since, byte[] Version);
}
