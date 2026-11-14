using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Reads only history explicitly authorized by the current device and template.</summary>
internal sealed class PublicDeviceHistoryReader(IDbContextFactory<ResourcesDbContext> resources, IDbContextFactory<NetworkDbContext> network)
{
    public static bool Valid(DateTimeOffset from, DateTimeOffset to) => from.Offset == TimeSpan.Zero && to.Offset == TimeSpan.Zero &&
        from < to && to - from <= TimeSpan.FromDays(7);

    public async Task<PublicDeviceHistory?> ReadAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (id.Version != 7) return null;
        if (!Valid(from, to)) throw new ArgumentException("A UTC history window of at most seven days is required.");
        await using var db = await resources.CreateDbContextAsync(token);
        var before = await AuthorizationAsync(db, id, token);
        if (before is null || before.DeviceSince == Device.PendingPublicSince || before.HistorySince == PublicationTemplate.PendingHistorySince ||
            !PublicationPolicy.TryValidateName(before.Name, out _)) return null;
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var start = from;
        if (start < before.DeviceSince) start = before.DeviceSince;
        if (start < before.HistorySince) start = before.HistorySince;
        var oldest = now.AddDays(-before.HistoryDays);
        if (start < oldest) start = oldest;
        var end = to < now ? to : now;
        var result = await DeviceHistoryBuckets.ReadAsync(network, id, before.Target, start, end, token);
        var after = await AuthorizationAsync(db, id, token);
        return after is not null && before.TemplateId == after.TemplateId && before.DeviceSince == after.DeviceSince && before.HistorySince == after.HistorySince &&
            before.DeviceVersion.AsSpan().SequenceEqual(after.DeviceVersion) && before.TemplateVersion.AsSpan().SequenceEqual(after.TemplateVersion) ? result : null;
    }

    private static Task<Authorization?> AuthorizationAsync(ResourcesDbContext db, Guid id, CancellationToken token) =>
        db.Database.SqlQuery<Authorization>($"""
            SELECT d.[PublicDisplayName] AS [Name], d.[Address] AS [Target], d.[PublicSince] AS [DeviceSince], d.[Version] AS [DeviceVersion],
                   t.[Id] AS [TemplateId], t.[Version] AS [TemplateVersion], t.[HistoryDays], t.[HistorySince]
            FROM [resources].[Devices] AS d
            JOIN [publishing].[Templates] AS t ON t.[Id] = d.[PublicTemplateId] AND t.[IsRetired] = 0
            WHERE d.[Id] = {id} AND d.[IsPublic] = 1 AND d.[IsArchived] = 0 AND d.[PublicDisplayName] IS NOT NULL
              AND d.[PublicSince] IS NOT NULL AND d.[PublicPolicyVersion] COLLATE Latin1_General_100_BIN2 = {PublicationPolicy.TemplateVersion}
              AND DATALENGTH(d.[PublicPolicyVersion]) = DATALENGTH({PublicationPolicy.TemplateVersion})
              AND t.[HistoryDays] BETWEEN 1 AND 7 AND t.[HistorySince] IS NOT NULL
            """).SingleOrDefaultAsync(token);

    private sealed class Authorization
    {
        public string Name { get; set; } = "";
        public string Target { get; set; } = "";
        public DateTimeOffset DeviceSince { get; set; }
        public byte[] DeviceVersion { get; set; } = [];
        public Guid TemplateId { get; set; }
        public byte[] TemplateVersion { get; set; } = [];
        public int HistoryDays { get; set; }
        public DateTimeOffset HistorySince { get; set; }
    }
}
