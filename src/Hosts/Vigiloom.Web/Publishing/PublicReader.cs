using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Projects explicitly published information without disclosing internal resource DTOs.</summary>
internal sealed class PublicReader(IDbContextFactory<ResourcesDbContext> resources, IDbContextFactory<ServiceObservabilityDbContext> services,
    PingStore ping, HealthStatusReader health)
{
    public const int PageSize = 50;
    public const int MaximumPage = 1000;

    public async Task<PublicPage<PublicGroup>> GroupsAsync(string? search, int page, CancellationToken token)
    {
        await using var db = await resources.CreateDbContextAsync(token);
        var query = GroupQuery(db);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Name.Contains(search));
        var rows = await query.OrderBy(x => x.Name).ThenBy(x => x.RowId).Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        var ids = rows.Select(x => x.Id).ToArray();
        // A member's publication can change without touching the group row. Recalculate both
        // visibility and counts before projecting; checking only the group version leaks stale counts.
        var current = await GroupQuery(db).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        var checkedRows = rows.Where(x => current.TryGetValue(x.Id, out var latest) && Same(latest.Version, x.Version) &&
            PublicationPolicy.TryValidateName(latest.Name, out _)).Select(x => current[x.Id]).ToArray();
        return new(checkedRows.Take(PageSize).Select(Map).ToArray(), page, checkedRows.Length > PageSize);
    }

    public async Task<PublicGroup?> GroupAsync(Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        await using var db = await resources.CreateDbContextAsync(token);
        var row = await GroupQuery(db).Where(x => x.Id == id).SingleOrDefaultAsync(token);
        if (row is null || !PublicationPolicy.TryValidateName(row.Name, out _)) return null;
        var current = await GroupQuery(db).Where(x => x.Id == id).SingleOrDefaultAsync(token);
        return current is not null && Same(row.Version, current.Version) && PublicationPolicy.TryValidateName(current.Name, out _) ? Map(current) : null;
    }

    private static IQueryable<GroupRow> GroupQuery(ResourcesDbContext db) => db.Database.SqlQuery<GroupRow>($"""
        SELECT g.[Id], g.[PublicDisplayName] AS [Name], g.[RowId], g.[Version],
          (SELECT COUNT(*) FROM [resources].[DeviceGroups] AS m JOIN [resources].[Devices] AS d ON d.[Id] = m.[DeviceId]
            WHERE m.[GroupId] = g.[Id] AND d.[IsPublic] = CAST(1 AS bit) AND d.[IsArchived] = CAST(0 AS bit)
              AND d.[PublicPolicyVersion] COLLATE Latin1_General_100_BIN2 = {PublicationPolicy.BaselineVersion} AND DATALENGTH(d.[PublicPolicyVersion]) = DATALENGTH({PublicationPolicy.BaselineVersion}) AND d.[PublicDisplayName] IS NOT NULL) AS [DeviceCount],
          (SELECT COUNT(*) FROM [resources].[ServiceGroups] AS m JOIN [service].[Services] AS s ON s.[Id] = m.[ServiceId]
            WHERE m.[GroupId] = g.[Id] AND s.[IsPublic] = CAST(1 AS bit)
              AND s.[PublicPolicyVersion] COLLATE Latin1_General_100_BIN2 = {PublicationPolicy.BaselineVersion} AND DATALENGTH(s.[PublicPolicyVersion]) = DATALENGTH({PublicationPolicy.BaselineVersion}) AND s.[PublicDisplayName] IS NOT NULL) AS [ServiceCount]
        FROM [resources].[Groups] AS g
        WHERE g.[IsPublic] = CAST(1 AS bit) AND g.[PublicPolicyVersion] COLLATE Latin1_General_100_BIN2 = {PublicationPolicy.BaselineVersion} AND DATALENGTH(g.[PublicPolicyVersion]) = DATALENGTH({PublicationPolicy.BaselineVersion}) AND g.[PublicDisplayName] IS NOT NULL
          AND (EXISTS (SELECT 1 FROM [resources].[DeviceGroups] AS m JOIN [resources].[Devices] AS d ON d.[Id] = m.[DeviceId]
                WHERE m.[GroupId] = g.[Id] AND d.[IsPublic] = CAST(1 AS bit) AND d.[IsArchived] = CAST(0 AS bit)
                  AND d.[PublicPolicyVersion] COLLATE Latin1_General_100_BIN2 = {PublicationPolicy.BaselineVersion} AND DATALENGTH(d.[PublicPolicyVersion]) = DATALENGTH({PublicationPolicy.BaselineVersion}) AND d.[PublicDisplayName] IS NOT NULL)
            OR EXISTS (SELECT 1 FROM [resources].[ServiceGroups] AS m JOIN [service].[Services] AS s ON s.[Id] = m.[ServiceId]
                WHERE m.[GroupId] = g.[Id] AND s.[IsPublic] = CAST(1 AS bit)
                  AND s.[PublicPolicyVersion] COLLATE Latin1_General_100_BIN2 = {PublicationPolicy.BaselineVersion} AND DATALENGTH(s.[PublicPolicyVersion]) = DATALENGTH({PublicationPolicy.BaselineVersion}) AND s.[PublicDisplayName] IS NOT NULL))
        """);

    public async Task<PublicPage<PublicDevice>> DevicesAsync(string? search, int page, Guid? groupId, CancellationToken token)
    {
        await using var db = await resources.CreateDbContextAsync(token);
        var groupVersion = groupId is { } requestedGroup ? await GroupVersionAsync(requestedGroup, token) : null;
        if (groupId is not null && groupVersion is null) return new([], page, false);
        var query = db.Devices.AsNoTracking().Where(x => x.IsPublic && !x.IsArchived && EF.Functions.Collate(x.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(x.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2 && x.PublicDisplayName != null);
        if (groupId is { } group)
            query = query.Where(x => db.DeviceGroups.Any(m => m.DeviceId == x.Id && m.GroupId == group) &&
                db.Groups.Any(g => g.Id == group && g.IsPublic && EF.Functions.Collate(g.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(g.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2 && g.PublicDisplayName != null));
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.PublicDisplayName!.Contains(search));
        var rows = await query.OrderBy(x => x.PublicDisplayName).ThenBy(x => x.RowId).Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new DeviceRow(x.Id, x.PublicDisplayName!, x.Address, x.IsEnabled, x.Version)).ToArrayAsync(token);
        var status = await ping.GetStatusesAsync(rows.Where(x => x.Enabled).Select(x => new PingDeviceSnapshot(x.Id, x.Version, [x.Target])).ToArray(), token);
        var ids = rows.Select(x => x.Id).ToArray();
        var currentQuery = db.Devices.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsPublic && !x.IsArchived && EF.Functions.Collate(x.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(x.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2);
        if (groupId is { } selectedGroup) currentQuery = currentQuery.Where(x => db.DeviceGroups.Any(m => m.DeviceId == x.Id && m.GroupId == selectedGroup));
        var current = await currentQuery
            .Select(x => new { x.Id, x.Version }).ToDictionaryAsync(x => x.Id, x => x.Version, token);
        if (groupId is { } finalGroup && !Same(groupVersion, await GroupVersionAsync(finalGroup, token))) return new([], page, false);
        var checkedRows = rows.Where(x => current.TryGetValue(x.Id, out var version) && version.AsSpan().SequenceEqual(x.Version) && PublicationPolicy.TryValidateName(x.Name, out _)).ToArray();
        var items = checkedRows.Take(PageSize).Select(row => Map(row, status)).ToArray();
        return new(items, page, checkedRows.Length > PageSize);
    }

    public async Task<PublicDevice?> DeviceAsync(Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        await using var db = await resources.CreateDbContextAsync(token);
        var row = await db.Devices.AsNoTracking().Where(x => x.Id == id && x.IsPublic && !x.IsArchived && EF.Functions.Collate(x.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(x.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2 && x.PublicDisplayName != null)
            .Select(x => new DeviceRow(x.Id, x.PublicDisplayName!, x.Address, x.IsEnabled, x.Version)).SingleOrDefaultAsync(token);
        if (row is null || !PublicationPolicy.TryValidateName(row.Name, out _)) return null;
        var states = await ping.GetStatusesAsync(row.Enabled ? [new PingDeviceSnapshot(row.Id, row.Version, [row.Target])] : [], token);
        var version = await db.Devices.AsNoTracking().Where(x => x.Id == id && x.IsPublic && !x.IsArchived && EF.Functions.Collate(x.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(x.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2)
            .Select(x => x.Version).SingleOrDefaultAsync(token);
        return version is not null && version.AsSpan().SequenceEqual(row.Version) ? Map(row, states) : null;
    }

    public async Task<PublicPage<PublicService>> ServicesAsync(string? search, int page, Guid? groupId, CancellationToken token)
    {
        await using var db = await services.CreateDbContextAsync(token);
        var groupVersion = groupId is { } requestedGroup ? await GroupVersionAsync(requestedGroup, token) : null;
        if (groupId is not null && groupVersion is null) return new([], page, false);
        var query = db.Services.AsNoTracking().Where(x => x.IsPublic && EF.Functions.Collate(x.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(x.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2 && x.PublicDisplayName != null);
        if (groupId is { } group)
        {
            // Group membership is owned by Resources; the host coordinates the two schemas in the same SQL database.
            var groupServices = db.Database.SqlQuery<Guid>($"""
                SELECT m.[ServiceId] AS [Value] FROM [resources].[ServiceGroups] AS m
                JOIN [resources].[Groups] AS g ON g.[Id] = m.[GroupId]
                WHERE g.[Id] = {group} AND g.[IsPublic] = CAST(1 AS bit) AND g.[PublicPolicyVersion] COLLATE Latin1_General_100_BIN2 = {PublicationPolicy.BaselineVersion} AND DATALENGTH(g.[PublicPolicyVersion]) = DATALENGTH({PublicationPolicy.BaselineVersion})
                  AND g.[PublicDisplayName] IS NOT NULL
                """);
            query = query.Where(x => groupServices.Contains(x.Id));
        }
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.PublicDisplayName!.Contains(search));
        var rows = await (from service in query
                          join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                          orderby service.PublicDisplayName, service.RowId
                          select new ServiceRow(service.Id, service.PublicDisplayName!, service.Version, source.Id, source.Version))
            .Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        var states = await health.ReadManyAsync(rows.Select(x => x.Id).ToArray(), token);
        var ids = rows.Select(x => x.Id).ToArray();
        var current = await (from service in db.Services.AsNoTracking()
                             join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                             where ids.Contains(service.Id) && service.IsPublic && EF.Functions.Collate(service.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(service.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2
                             select new { service.Id, service.Version, SourceVersion = source.Version }).ToDictionaryAsync(x => x.Id, token);
        HashSet<Guid>? members = null;
        if (groupId is { } finalGroup)
        {
            await using var resourcesDb = await resources.CreateDbContextAsync(token);
            members = (await resourcesDb.ServiceGroups.AsNoTracking().Where(x => x.GroupId == finalGroup && ids.Contains(x.ServiceId)).Select(x => x.ServiceId).ToArrayAsync(token)).ToHashSet();
            if (!Same(groupVersion, await GroupVersionAsync(finalGroup, token))) return new([], page, false);
        }
        var checkedRows = rows.Where(x => current.TryGetValue(x.Id, out var entry) && entry.Version.AsSpan().SequenceEqual(x.Version) &&
            entry.SourceVersion.AsSpan().SequenceEqual(x.SourceVersion) && (members is null || members.Contains(x.Id)) && PublicationPolicy.TryValidateName(x.Name, out _) && states.ContainsKey(x.Id)).ToArray();
        return new(checkedRows.Take(PageSize).Select(x => Map(x, states[x.Id])).ToArray(), page, checkedRows.Length > PageSize);
    }

    public async Task<PublicService?> ServiceAsync(Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        await using var db = await services.CreateDbContextAsync(token);
        var row = await (from service in db.Services.AsNoTracking()
                         join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                         where service.Id == id && service.IsPublic && EF.Functions.Collate(service.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(service.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2 && service.PublicDisplayName != null
                         select new ServiceRow(service.Id, service.PublicDisplayName!, service.Version, source.Id, source.Version)).SingleOrDefaultAsync(token);
        if (row is null || !PublicationPolicy.TryValidateName(row.Name, out _)) return null;
        var states = await health.ReadManyAsync([id], token);
        var current = await (from service in db.Services.AsNoTracking()
                             join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                             where service.Id == id && service.IsPublic && EF.Functions.Collate(service.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(service.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2
                             select new { service.Version, SourceVersion = source.Version }).SingleOrDefaultAsync(token);
        return current is not null && current.Version.AsSpan().SequenceEqual(row.Version) && current.SourceVersion.AsSpan().SequenceEqual(row.SourceVersion) &&
            states.TryGetValue(id, out var state) ? Map(row, state) : null;
    }

    private static PublicDevice Map(DeviceRow row, IReadOnlyDictionary<Guid, DevicePingStatus> states)
    {
        if (!row.Enabled || !states.TryGetValue(row.Id, out var status)) return new(row.Id, row.Name, "Unknown", null, null);
        var target = status.Targets[0];
        var state = target.State switch { Reachability.Reachable => "Operational", Reachability.Unreachable => "Unavailable", _ => "Unknown" };
        var latency = target.State == Reachability.Reachable && target.LastOutcome == PingOutcome.Success && !target.Stale ? target.LatencyMilliseconds : null;
        return new(row.Id, row.Name, state, latency, target.MeasuredAt);
    }

    private static PublicService Map(ServiceRow row, HealthStatusReader.OverviewStatus status) =>
        new(row.Id, row.Name, status.CollectionStopped || status.SourceRevoked ? "Unknown" : status.Status.State switch
        { ServiceHealthState.Healthy => "Healthy", ServiceHealthState.Degraded => "Degraded", ServiceHealthState.Unhealthy => "Unhealthy", _ => "Unknown" });

    private async Task<byte[]?> GroupVersionAsync(Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        await using var db = await resources.CreateDbContextAsync(token);
        var group = await db.Groups.AsNoTracking().Where(x => x.Id == id && x.IsPublic && EF.Functions.Collate(x.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.BaselineVersion && EF.Functions.DataLength(x.PublicPolicyVersion) == PublicationPolicy.BaselineVersion.Length * 2 && x.PublicDisplayName != null)
            .Select(x => new { x.PublicDisplayName, x.Version }).SingleOrDefaultAsync(token);
        return group is not null && PublicationPolicy.TryValidateName(group.PublicDisplayName, out _) ? group.Version : null;
    }
    private static bool Same(byte[]? left, byte[]? right) => left is not null && right is not null && left.AsSpan().SequenceEqual(right);
    private static PublicGroup Map(GroupRow row) => new(row.Id, row.Name, row.DeviceCount, row.ServiceCount);

    private sealed record DeviceRow(Guid Id, string Name, string Target, bool Enabled, byte[] Version);
    private sealed record ServiceRow(Guid Id, string Name, byte[] Version, Guid SourceId, byte[] SourceVersion);
    private sealed class GroupRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public long RowId { get; set; }
        public byte[] Version { get; set; } = [];
        public int DeviceCount { get; set; }
        public int ServiceCount { get; set; }
    }
}
