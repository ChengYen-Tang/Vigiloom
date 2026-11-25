using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Groups;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Manages group membership against the resource-owned application catalogue.</summary>
public sealed class ServiceGroupManagement(IDbContextFactory<ResourcesDbContext> contexts, GroupManagement groups, TimeProvider clock, GroupAuditOutbox audit)
{
    public async Task<ServiceGroupLinkPage> ListGroupsAsync(Guid serviceId, int page, CancellationToken token)
    {
        const int pageSize = 20;
        page = Math.Clamp(page, 1, 1000);
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await (from membership in db.ServiceGroups.AsNoTracking()
                          join g in db.Groups.AsNoTracking() on membership.GroupId equals g.Id
                          where membership.ServiceId == serviceId
                          orderby g.Name, g.RowId
                          select new ServiceGroupLink(g.Id, g.Name))
            .Skip((page - 1) * pageSize).Take(pageSize + 1).ToArrayAsync(token);
        return new(rows.Take(pageSize).ToArray(), page < 1000 && rows.Length > pageSize);
    }

    public async Task<GroupServicePage> ListAsync(Guid? groupId, bool ungrouped, bool? member, string? search, int page, CancellationToken token)
    {
        page = Math.Clamp(page, 1, int.MaxValue / GroupManagement.PageSize);
        search = search?.Trim();
        await using var db = await contexts.CreateDbContextAsync(token);
        var query = db.ApplicationServices.AsNoTracking();
        if (!string.IsNullOrEmpty(search)) query = query.Where(s => s.DisplayName.Contains(search) || s.Environment.Contains(search));
        if (ungrouped) query = query.Where(s => !db.ServiceGroups.Any(m => m.ServiceId == s.Id));
        if (groupId is not null && member is true) query = query.Where(s => db.ServiceGroups.Any(m => m.GroupId == groupId && m.ServiceId == s.Id));
        if (groupId is not null && member is false) query = query.Where(s => !db.ServiceGroups.Any(m => m.GroupId == groupId && m.ServiceId == s.Id));
        var total = await query.CountAsync(token);
        var items = await query.OrderBy(s => s.DisplayName).ThenBy(s => s.RowId)
            .Skip((page - 1) * GroupManagement.PageSize).Take(GroupManagement.PageSize)
            .Select(s => new GroupService(s.Id, s.DisplayName, s.Environment,
                groupId != null && db.ServiceGroups.Any(m => m.GroupId == groupId && m.ServiceId == s.Id))).ToArrayAsync(token);
        return new(items, page, GroupManagement.PageSize, total);
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
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var group = await db.Groups.FromSqlInterpolated($"SELECT * FROM [resources].[Groups] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
        if (group is null) return new(GroupResultStatus.NotFound);
        if (!group.Version.AsSpan().SequenceEqual(version)) return new(GroupResultStatus.Conflict);
        var count = await db.ApplicationServices.Where(s => addIds.Contains(s.Id)).CountAsync(token);
        if (count != addIds.Length) return Invalid("members", "服務不存在，請重新整理後再試。");
        var existing = await db.ServiceGroups.Where(m => m.GroupId == id && (addIds.Contains(m.ServiceId) || removeIds.Contains(m.ServiceId))).ToListAsync(token);
        var present = existing.Select(m => m.ServiceId).ToHashSet();
        db.ServiceGroups.AddRange(addIds.Where(serviceId => !present.Contains(serviceId)).Select(serviceId => new ServiceGroup(id, serviceId)));
        db.ServiceGroups.RemoveRange(existing.Where(m => removeIds.Contains(m.ServiceId)));
        var now = clock.GetUtcNow().ToUniversalTime();
        group.Touch(now);
        db.Entry(group).Property(g => g.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(token);
        await audit.CommitAsync(db, new OperationAudit(actor, id, now, new GroupChanged(GroupAction.ServicesChanged,
            addIds.Count(x => !present.Contains(x)), existing.Count(x => removeIds.Contains(x.ServiceId)))), token);
        return new(GroupResultStatus.Success, await groups.GetAsync(id, token));
    }

    private static GroupResult Invalid(string field, string message) =>
        new(GroupResultStatus.Invalid, Errors: new Dictionary<string, string[]> { [field] = [message] });
}
