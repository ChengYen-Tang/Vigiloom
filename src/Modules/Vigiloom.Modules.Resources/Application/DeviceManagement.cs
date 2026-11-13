using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public enum DeviceResultStatus { Success, NotFound, Conflict, Invalid }
public sealed record DeviceResult(DeviceResultStatus Status, DeviceDetails? Device = null, IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>Owns device state and SQL transactions. Callers must authorize the current principal.</summary>
public sealed class DeviceManagement(IDbContextFactory<ResourcesDbContext> contexts, TimeProvider clock, IOperationAuditWriter<OperationAudit>? audit = null)
{
    public const int PageSize = 50;
    public const int MaxAdditionalTargets = 16;

    public async Task<DevicePage> ListAsync(string? search, string? sort, bool descending, bool archived, int page, CancellationToken token)
    {
        page = Math.Clamp(page, 1, int.MaxValue / PageSize);
        search = search?.Trim();
        await using var db = await contexts.CreateDbContextAsync(token);
        var query = db.Devices.AsNoTracking().Where(x => x.IsArchived == archived);
        if (!string.IsNullOrEmpty(search)) query = query.Where(x => x.Name.Contains(search) || x.Address.Contains(search) ||
            db.DeviceTargets.Any(t => t.DeviceId == x.Id && t.Address.Contains(search)));
        var total = await query.CountAsync(token);
        var ordered = (sort, descending) switch
        {
            ("address", false) => query.OrderBy(x => x.Address),
            ("address", true) => query.OrderByDescending(x => x.Address),
            ("created", false) => query.OrderBy(x => x.CreatedAt),
            ("created", true) => query.OrderByDescending(x => x.CreatedAt),
            (_, true) => query.OrderByDescending(x => x.Name),
            _ => query.OrderBy(x => x.Name)
        };
        var devices = await ordered.ThenBy(x => x.RowId).Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(token);
        var ids = devices.Select(x => x.Id).ToArray();
        var targets = await db.DeviceTargets.AsNoTracking().Where(x => ids.Contains(x.DeviceId)).OrderBy(x => x.Address).ToListAsync(token);
        var lookup = targets.ToLookup(x => x.DeviceId, x => x.Address);
        return new DevicePage(devices.Select(x => Map(x, lookup[x.Id])).ToArray(), page, PageSize, total);
    }

    public async Task<DeviceDetails?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (device is null) return null;
        var targets = await db.DeviceTargets.AsNoTracking().Where(x => x.DeviceId == id).OrderBy(x => x.Address).Select(x => x.Address).ToArrayAsync(token);
        return Map(device, targets);
    }

    public Task<IReadOnlyList<DeviceDetails>> FindAddressMatchesAsync(string address, Guid? excludeId, CancellationToken token) =>
        FindAddressMatchesAsync([address], excludeId, token);

    public async Task<IReadOnlyList<DeviceDetails>> FindAddressMatchesAsync(IEnumerable<string> addresses, Guid? excludeId, CancellationToken token)
    {
        var normalized = addresses.Take(MaxAdditionalTargets + 1).Select(value => TargetAddress.TryNormalize(value, out var result) ? result : null)
            .Where(value => value is not null).Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0) return [];
        await using var db = await contexts.CreateDbContextAsync(token);
        var devices = await db.Devices.AsNoTracking().Where(x => !x.IsArchived && x.Id != excludeId &&
            (normalized.Contains(x.Address) || db.DeviceTargets.Any(t => t.DeviceId == x.Id && normalized.Contains(t.Address))))
            .OrderBy(x => x.Name).ThenBy(x => x.RowId).Take(10).ToListAsync(token);
        return devices.Select(x => Map(x, [])).ToArray();
    }

    public async Task<DeviceResult> CreateAsync(DeviceInput input, Guid actor, CancellationToken token)
    {
        var validation = Validate(input, allowPublic: true);
        if (input.Public && audit is null) validation.Errors["public"] = ["公開設定目前無法使用。"];
        if (actor == Guid.Empty) throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        if (validation.Errors.Count != 0) return new(DeviceResultStatus.Invalid, Errors: validation.Errors);
        var now = clock.GetUtcNow();
        var device = new Device(validation.Name, validation.Address, input.DeepMonitoring, now);
        if (!input.Enabled) device.Update(validation.Name, validation.Address, input.DeepMonitoring, false, now);
        if (input.Public) { PublicationPolicy.TryValidateName(input.PublicDisplayName, out var publicName); device.SetPublication(true, publicName, input.PublicPolicyVersion, now); }
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = input.Public ? await db.Database.BeginTransactionAsync(token) : null;
        db.Devices.Add(device);
        db.DeviceTargets.AddRange(validation.Targets.Select(address => new DeviceTarget(device.Id, address)));
        db.DeviceAudits.Add(new DeviceAudit(device.Id, actor, "created", now));
        await db.SaveChangesAsync(token);
        if (input.Public)
        {
            await audit!.AppendAsync(db.Database.GetDbConnection(), tx!.GetDbTransaction(),
                new OperationAudit(actor, device.Id, now.ToUniversalTime(), new PublicationChanged(PublicationResourceKind.Device, false, true, true)), token);
            await tx!.CommitAsync(token);
        }
        return new(DeviceResultStatus.Success, Map(device, validation.Targets));
    }

    public async Task<DeviceResult> UpdateAsync(Guid id, DeviceInput input, Guid actor, CancellationToken token)
    {
        var validation = Validate(input);
        if (!TryVersion(input.Version, out var version)) validation.Errors["version"] = ["請重新整理後再試。"];
        if (actor == Guid.Empty) throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        if (validation.Errors.Count != 0) return new(DeviceResultStatus.Invalid, Errors: validation.Errors);
        await using var db = await contexts.CreateDbContextAsync(token);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == id, token);
        if (device is null) return new(DeviceResultStatus.NotFound);
        if (device.IsArchived) return new(DeviceResultStatus.Conflict);
        db.Entry(device).Property(x => x.Version).OriginalValue = version;
        var existing = await db.DeviceTargets.Where(x => x.DeviceId == id).ToListAsync(token);
        var desired = validation.Targets.ToHashSet(StringComparer.Ordinal);
        db.DeviceTargets.RemoveRange(existing.Where(x => !desired.Contains(x.Address)));
        var current = existing.Select(x => x.Address).ToHashSet(StringComparer.Ordinal);
        db.DeviceTargets.AddRange(desired.Where(x => !current.Contains(x)).Select(x => new DeviceTarget(id, x)));
        var now = clock.GetUtcNow();
        device.Update(validation.Name, validation.Address, input.DeepMonitoring, input.Enabled, now);
        db.DeviceAudits.Add(new DeviceAudit(id, actor, "updated", now));
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return new(DeviceResultStatus.Conflict); }
        return new(DeviceResultStatus.Success, Map(device, validation.Targets));
    }

    public async Task<DeviceResult> SetArchivedAsync(Guid id, bool archive, string? expectedVersion, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty) throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        if (!TryVersion(expectedVersion, out var version)) return new(DeviceResultStatus.Invalid, Errors: new Dictionary<string, string[]> { ["version"] = ["請重新整理後再試。"] });
        await using var db = await contexts.CreateDbContextAsync(token);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == id, token);
        if (device is null) return new(DeviceResultStatus.NotFound);
        if (device.IsArchived == archive) return new(DeviceResultStatus.Conflict);
        db.Entry(device).Property(x => x.Version).OriginalValue = version;
        var now = clock.GetUtcNow();
        if (archive) device.Archive(now); else device.Restore(now);
        db.DeviceAudits.Add(new DeviceAudit(id, actor, archive ? "archived" : "restored", now));
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return new(DeviceResultStatus.Conflict); }
        var targets = await db.DeviceTargets.AsNoTracking().Where(x => x.DeviceId == id).OrderBy(x => x.Address).Select(x => x.Address).ToArrayAsync(token);
        return new(DeviceResultStatus.Success, Map(device, targets));
    }

    private static DeviceDetails Map(Device device, IEnumerable<string> targets) => new(device.Id, device.Name, device.Address,
        targets.ToArray(), device.DeepMonitoring, device.IsEnabled, device.IsArchived, device.CreatedAt, device.UpdatedAt, Convert.ToBase64String(device.Version));

    private static bool TryVersion(string? text, out byte[] version)
    {
        version = [];
        if (string.IsNullOrEmpty(text)) return false;
        try { version = Convert.FromBase64String(text); return version.Length == 8; }
        catch (FormatException) { return false; }
    }

    private static Validation Validate(DeviceInput input, bool allowPublic = false)
    {
        var result = new Validation();
        if (input.Public && (!allowPublic || input.PublicPolicyVersion != PublicationPolicy.BaselineVersion || !PublicationPolicy.TryValidateName(input.PublicDisplayName, out _)))
            result.Errors["public"] = ["請確認公開名稱與範本，名稱不可包含 IP 位址。"];
        result.Name = input.Name?.Trim() ?? "";
        if (result.Name.Length is 0 or > 256) result.Errors["name"] = ["請輸入 1 至 256 個字元的設備名稱。"];
        if (!TargetAddress.TryNormalize(input.Address, out var address)) result.Errors["address"] = ["請輸入有效的 IPv4、IPv6 或 DNS 名稱。"];
        result.Address = address;
        var targets = input.AdditionalTargets ?? [];
        if (targets.Length > MaxAdditionalTargets) result.Errors["additionalTargets"] = [$"額外目標最多 {MaxAdditionalTargets} 個。"];
        else
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            if (address.Length != 0) unique.Add(address);
            foreach (var target in targets)
            {
                if (!TargetAddress.TryNormalize(target, out var normalized) || !unique.Add(normalized))
                {
                    result.Errors["additionalTargets"] = ["額外目標須為有效且不重複的 IPv4、IPv6 或 DNS 名稱。"];
                    break;
                }
                result.Targets.Add(normalized);
            }
        }
        return result;
    }

    private sealed class Validation
    {
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public List<string> Targets { get; } = [];
        public Dictionary<string, string[]> Errors { get; } = [];
    }
}
