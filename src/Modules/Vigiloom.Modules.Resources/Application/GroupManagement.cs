using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Groups;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public enum GroupResultStatus { Success, NotFound, Conflict, Invalid }
public sealed record GroupResult(GroupResultStatus Status, GroupDetails? Group = null, IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>Owns group state and membership transactions. Callers must authorize access.</summary>
public sealed class GroupManagement(IDbContextFactory<ResourcesDbContext> contexts, TimeProvider clock, IOperationAuditWriter<OperationAudit> audit)
{
    public const int PageSize = 50;
    public const int MaxBatchSize = 100;

    public async Task<GroupPage> ListAsync(string? search, int page, CancellationToken token)
    {
        page = Math.Clamp(page, 1, int.MaxValue / PageSize);
        search = search?.Trim();
        await using var db = await contexts.CreateDbContextAsync(token);
        var query = db.Groups.AsNoTracking();
        if (!string.IsNullOrEmpty(search)) query = query.Where(x => x.Name.Contains(search) || (x.Description != null && x.Description.Contains(search)));
        var total = await query.CountAsync(token);
        var rows = await Project(db, query.OrderBy(x => x.Name).ThenBy(x => x.RowId).Skip((page - 1) * PageSize).Take(PageSize)).ToArrayAsync(token);
        return new(rows.Select(Map).ToArray(), page, PageSize, total);
    }

    public async Task<GroupDetails?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await Project(db, db.Groups.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(token);
        return row is null ? null : Map(row);
    }

    /// <summary>Lists active devices; ungrouped means no memberships at all.</summary>
    public async Task<GroupDevicePage> ListDevicesAsync(Guid? groupId, bool ungrouped, bool? member, string? search, int page, CancellationToken token)
    {
        page = Math.Clamp(page, 1, int.MaxValue / PageSize);
        search = search?.Trim();
        await using var db = await contexts.CreateDbContextAsync(token);
        var devices = db.Devices.AsNoTracking().Where(x => !x.IsArchived);
        if (!string.IsNullOrEmpty(search)) devices = devices.Where(x => x.Name.Contains(search) || x.Address.Contains(search));
        if (ungrouped) devices = devices.Where(x => !db.DeviceGroups.Any(m => m.DeviceId == x.Id));
        if (groupId is not null && member is true) devices = devices.Where(x => db.DeviceGroups.Any(m => m.GroupId == groupId && m.DeviceId == x.Id));
        if (groupId is not null && member is false) devices = devices.Where(x => !db.DeviceGroups.Any(m => m.GroupId == groupId && m.DeviceId == x.Id));
        var total = await devices.CountAsync(token);
        var items = await devices.OrderBy(x => x.Name).ThenBy(x => x.RowId).Skip((page - 1) * PageSize).Take(PageSize)
            .Select(x => new GroupDevice(x.Id, x.Name, x.Address, x.DeepMonitoring, x.IsEnabled,
                groupId != null && db.DeviceGroups.Any(m => m.GroupId == groupId && m.DeviceId == x.Id))).ToArrayAsync(token);
        return new(items, page, PageSize, total);
    }

    public async Task<GroupResult> CreateAsync(GroupInput input, Guid actor, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(actor, Guid.Empty);
        var (name, description, errors) = Validate(input, requireVersion: false);
        if (errors.Count != 0) return new(GroupResultStatus.Invalid, Errors: errors);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var now = clock.GetUtcNow().ToUniversalTime();
        var group = new Group(name, description, now);
        db.Groups.Add(group);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (IsDuplicate(ex)) { return DuplicateName(); }
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), new OperationAudit(actor, group.Id, now, new GroupChanged(GroupAction.Created, 0, 0)), token);
        await transaction.CommitAsync(token);
        return new(GroupResultStatus.Success, Map(new(group, 0, 0, 0)));
    }

    public async Task<GroupResult> UpdateAsync(Guid id, GroupInput input, Guid actor, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(actor, Guid.Empty);
        var (name, description, errors) = Validate(input, requireVersion: true);
        if (errors.Count != 0) return new(GroupResultStatus.Invalid, Errors: errors);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var group = await db.Groups.SingleOrDefaultAsync(x => x.Id == id, token);
        if (group is null) return new(GroupResultStatus.NotFound);
        db.Entry(group).Property(x => x.Version).OriginalValue = Convert.FromBase64String(input.Version!);
        var now = clock.GetUtcNow().ToUniversalTime();
        group.Update(name, description, now);
        // A successful write must advance rowversion even if the timestamp has the same precision.
        db.Entry(group).Property(x => x.UpdatedAt).IsModified = true;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return new(GroupResultStatus.Conflict); }
        catch (DbUpdateException ex) when (IsDuplicate(ex)) { return DuplicateName(); }
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), new OperationAudit(actor, id, now, new GroupChanged(GroupAction.Updated, 0, 0)), token);
        await transaction.CommitAsync(token);
        return new(GroupResultStatus.Success, await GetAsync(id, token));
    }

    public async Task<GroupResult> ChangeMembersAsync(Guid id, GroupMembershipChange change, Guid actor, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(actor, Guid.Empty);
        if (!TryVersion(change.Version, out var version)) return Invalid("version", "請重新整理後再試。");
        var add = change.Add ?? [];
        var remove = change.Remove ?? [];
        if (add.Length + remove.Length > MaxBatchSize || add.Any(x => x == Guid.Empty) || remove.Any(x => x == Guid.Empty) || add.Intersect(remove).Any())
            return Invalid("members", $"每次最多處理 {MaxBatchSize} 筆，且同一設備不能同時加入及移出。");
        var addIds = add.Distinct().ToArray();
        var removeIds = remove.Distinct().ToArray();
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        // Lock the group for membership/deletion and retain device locks until commit, without
        // serializable gap locks on unrelated groups' membership ranges.
        var group = await db.Groups.FromSqlInterpolated($"SELECT * FROM [resources].[Groups] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
        if (group is null) return new(GroupResultStatus.NotFound);
        if (!group.Version.AsSpan().SequenceEqual(version)) return new(GroupResultStatus.Conflict);
        var found = await db.Devices.FromSqlRaw("SELECT * FROM [resources].[Devices] WITH (HOLDLOCK)")
            .Where(x => addIds.Contains(x.Id) && !x.IsArchived).Select(x => x.Id).ToArrayAsync(token);
        if (found.Length != addIds.Length) return Invalid("members", "設備不存在或已封存，請重新整理後再試。");
        var relevant = await db.DeviceGroups.Where(x => x.GroupId == id && (addIds.Contains(x.DeviceId) || removeIds.Contains(x.DeviceId))).ToListAsync(token);
        var present = relevant.Select(x => x.DeviceId).ToHashSet();
        db.DeviceGroups.AddRange(addIds.Where(x => !present.Contains(x)).Select(x => new DeviceGroup(id, x)));
        db.DeviceGroups.RemoveRange(relevant.Where(x => removeIds.Contains(x.DeviceId)));
        var now = clock.GetUtcNow().ToUniversalTime();
        group.Touch(now);
        db.Entry(group).Property(x => x.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, id, now, new GroupChanged(GroupAction.DevicesChanged, addIds.Count(x => !present.Contains(x)), relevant.Count(x => removeIds.Contains(x.DeviceId)))), token);
        await transaction.CommitAsync(token);
        return new(GroupResultStatus.Success, await GetAsync(id, token));
    }

    public async Task<GroupResult> DeleteAsync(Guid id, string? expectedVersion, Guid actor, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(actor, Guid.Empty);
        if (!TryVersion(expectedVersion, out var version)) return Invalid("version", "請重新整理後再試。");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var group = await db.Groups.FromSqlInterpolated($"SELECT * FROM [resources].[Groups] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
        if (group is null) return new(GroupResultStatus.NotFound);
        if (!group.Version.AsSpan().SequenceEqual(version)) return new(GroupResultStatus.Conflict);
        await db.DeviceGroups.Where(x => x.GroupId == id).ExecuteDeleteAsync(token);
        await db.ServiceGroups.Where(x => x.GroupId == id).ExecuteDeleteAsync(token);
        db.Groups.Remove(group);
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), new OperationAudit(actor, id, clock.GetUtcNow().ToUniversalTime(), new GroupChanged(GroupAction.Deleted, 0, 0)), token);
        await transaction.CommitAsync(token);
        return new(GroupResultStatus.Success);
    }

    private static IQueryable<GroupRow> Project(ResourcesDbContext db, IQueryable<Group> query) => query.Select(g => new GroupRow(g,
        db.DeviceGroups.Count(m => m.GroupId == g.Id && db.Devices.Any(d => d.Id == m.DeviceId && !d.IsArchived)),
        db.DeviceGroups.Count(m => m.GroupId == g.Id && db.Devices.Any(d => d.Id == m.DeviceId && d.IsArchived)),
        db.ServiceGroups.Count(m => m.GroupId == g.Id)));
    private static GroupDetails Map(GroupRow x) => new(x.Group.Id, x.Group.Name, x.Group.Description, x.ActiveCount, x.ArchivedCount,
        x.ServiceCount, x.Group.CreatedAt, x.Group.UpdatedAt, Convert.ToBase64String(x.Group.Version));
    private sealed record GroupRow(Group Group, int ActiveCount, int ArchivedCount, int ServiceCount);

    private static (string Name, string? Description, Dictionary<string, string[]> Errors) Validate(GroupInput input, bool requireVersion)
    {
        var name = input.Name?.Trim() ?? "";
        var description = input.Description?.Trim();
        if (description?.Length == 0) description = null;
        var errors = new Dictionary<string, string[]>();
        if (name.Length is 0 or > 256) errors["name"] = ["請輸入 1 至 256 個字元的群組名稱。"];
        if (description?.Length > 1024) errors["description"] = ["說明最多 1024 個字元。"];
        if (requireVersion && !TryVersion(input.Version, out _)) errors["version"] = ["請重新整理後再試。"];
        return (name, description, errors);
    }

    private static bool TryVersion(string? text, out byte[] version)
    {
        version = [];
        if (string.IsNullOrEmpty(text)) return false;
        try { version = Convert.FromBase64String(text); return version.Length == 8; }
        catch (FormatException) { return false; }
    }

    private static bool IsDuplicate(DbUpdateException exception) => exception.InnerException is SqlException { Number: 2601 or 2627 };
    private static GroupResult DuplicateName() => Invalid("name", "已有相同名稱的群組，請使用其他名稱。");
    private static GroupResult Invalid(string field, string message) => new(GroupResultStatus.Invalid, Errors: new Dictionary<string, string[]> { [field] = [message] });
}
