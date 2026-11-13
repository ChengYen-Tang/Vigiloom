using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Groups;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.Groups;

/// <summary>Coordinates service identities and group relationships without coupling the modules.</summary>
internal sealed class ServiceMembership(
    IDbContextFactory<ResourcesDbContext> resources,
    IDbContextFactory<ServiceObservabilityDbContext> services,
    GroupManagement groups,
    TimeProvider clock,
    IOperationAuditWriter<OperationAudit> audit)
{
    internal sealed record GroupLink(Guid Id, string Name);
    internal sealed record GroupLinkPage(IReadOnlyList<GroupLink> Items, bool HasMore);

    public async Task<GroupLinkPage> ListGroupsAsync(Guid serviceId, int page, CancellationToken token)
    {
        const int pageSize = 20;
        page = Math.Clamp(page, 1, 1000);
        await using var db = await resources.CreateDbContextAsync(token);
        var rows = await (from membership in db.ServiceGroups.AsNoTracking()
                          join g in db.Groups.AsNoTracking() on membership.GroupId equals g.Id
                          where membership.ServiceId == serviceId
                          orderby g.Name, g.RowId
                          select new GroupLink(g.Id, g.Name))
            .Skip((page - 1) * pageSize).Take(pageSize + 1).ToArrayAsync(token);
        return new(rows.Take(pageSize).ToArray(), page < 1000 && rows.Length > pageSize);
    }

    public async Task<GroupServicePage> ListAsync(Guid? groupId, bool ungrouped, bool? member, string? search, int page, CancellationToken token)
    {
        page = Math.Clamp(page, 1, int.MaxValue / GroupManagement.PageSize);
        search = search?.Trim();
        await using var db = await services.CreateDbContextAsync(token);
        // The host coordinates the two schemas. The owning modules do not reference each other.
        var query = db.Database.SqlQuery<ServiceRow>($"""
            SELECT s.[Id], s.[DisplayName] AS [Name], s.[Environment], s.[RowId],
                CASE WHEN EXISTS (SELECT 1 FROM [resources].[ServiceGroups] AS m
                    WHERE m.[ServiceId] = s.[Id] AND m.[GroupId] = {groupId})
                  THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS [IsMember]
            FROM [service].[Services] AS s
            WHERE ({ungrouped} = CAST(0 AS bit) OR NOT EXISTS
                (SELECT 1 FROM [resources].[ServiceGroups] AS m WHERE m.[ServiceId] = s.[Id]))
              AND ({groupId} IS NULL OR {member} IS NULL OR
                CASE WHEN EXISTS (SELECT 1 FROM [resources].[ServiceGroups] AS m
                    WHERE m.[ServiceId] = s.[Id] AND m.[GroupId] = {groupId})
                  THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END = {member})
            """);
        if (!string.IsNullOrEmpty(search)) query = query.Where(s => s.Name.Contains(search) || s.Environment.Contains(search));
        var total = await query.CountAsync(token);
        var rows = await query.OrderBy(s => s.Name).ThenBy(s => s.RowId)
            .Skip((page - 1) * GroupManagement.PageSize).Take(GroupManagement.PageSize)
            .Select(s => new GroupService(s.Id, s.Name, s.Environment, s.IsMember)).ToArrayAsync(token);
        return new(rows, page, GroupManagement.PageSize, total);
    }

    public async Task<GroupResult> ChangeAsync(Guid id, GroupMembershipChange change, Guid actor, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(actor, Guid.Empty);
        byte[] version;
        try { version = Convert.FromBase64String(change.Version ?? ""); }
        catch (FormatException) { return Invalid("version", "請重新整理後再試。"); }
        if (version.Length != 8) return Invalid("version", "請重新整理後再試。");
        var add = change.Add ?? [];
        var remove = change.Remove ?? [];
        if (add.Length + remove.Length > GroupManagement.MaxBatchSize || add.Any(x => x == Guid.Empty) ||
            remove.Any(x => x == Guid.Empty) || add.Intersect(remove).Any())
            return Invalid("members", "選取的服務無效或超過數量上限。");
        var addIds = add.Distinct().ToArray();
        var removeIds = remove.Distinct().ToArray();
        await using var db = await resources.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var group = await db.Groups.FromSqlInterpolated($"SELECT * FROM [resources].[Groups] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
        if (group is null) return new(GroupResultStatus.NotFound);
        if (!group.Version.AsSpan().SequenceEqual(version)) return new(GroupResultStatus.Conflict);

        await using var serviceDb = await services.CreateDbContextAsync(token);
        serviceDb.Database.SetDbConnection(db.Database.GetDbConnection());
        await serviceDb.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
        var found = await serviceDb.Services.FromSqlRaw("SELECT * FROM [service].[Services] WITH (HOLDLOCK)")
            .Where(s => addIds.Contains(s.Id)).Select(s => s.Id).CountAsync(token);
        if (found != addIds.Length) return Invalid("members", "服務不存在，請重新整理後再試。");
        var existing = await db.ServiceGroups.Where(m => m.GroupId == id && (addIds.Contains(m.ServiceId) || removeIds.Contains(m.ServiceId))).ToListAsync(token);
        var present = existing.Select(m => m.ServiceId).ToHashSet();
        db.ServiceGroups.AddRange(addIds.Where(serviceId => !present.Contains(serviceId)).Select(serviceId => new ServiceGroup(id, serviceId)));
        db.ServiceGroups.RemoveRange(existing.Where(m => removeIds.Contains(m.ServiceId)));
        var now = clock.GetUtcNow().ToUniversalTime();
        group.Touch(now);
        db.Entry(group).Property(g => g.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, id, now, new GroupChanged(GroupAction.ServicesChanged,
                addIds.Count(x => !present.Contains(x)), existing.Count(x => removeIds.Contains(x.ServiceId)))), token);
        await transaction.CommitAsync(token);
        return new(GroupResultStatus.Success, await groups.GetAsync(id, token));
    }

    private static GroupResult Invalid(string field, string message) =>
        new(GroupResultStatus.Invalid, Errors: new Dictionary<string, string[]> { [field] = [message] });

    private sealed class ServiceRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Environment { get; set; } = "";
        public long RowId { get; set; }
        public bool IsMember { get; set; }
    }
}
