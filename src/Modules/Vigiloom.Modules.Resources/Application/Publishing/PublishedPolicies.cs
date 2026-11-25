using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application.Publishing;

/// <summary>Reads current public authorization directly from the owning database.</summary>
public sealed class PublishedPolicies(IDbContextFactory<ResourcesDbContext> contexts)
{
    private const int PageSize = 50;

    public async Task<PublicPage<PublishedDevicePolicy>> DevicesAsync(string? search, int page, Guid? groupId, CancellationToken token)
    {
        ValidatePage(page);
        if (groupId is { } requestedGroup && await GroupAsync(requestedGroup, token) is null) return new([], page, false);
        await using var db = await contexts.CreateDbContextAsync(token);
        var devices = db.Devices.AsNoTracking().Where(d => d.IsPublic && !d.IsArchived && d.PublicDisplayName != null);
        if (groupId is { } group)
            devices = devices.Where(d => db.DeviceGroups.Any(m => m.DeviceId == d.Id && m.GroupId == group) &&
                db.Groups.Any(g => g.Id == group && g.IsPublic && g.PublicPolicyVersion == PublicationPolicy.BaselineVersion && g.PublicDisplayName != null));
        if (!string.IsNullOrWhiteSpace(search)) devices = devices.Where(d => d.PublicDisplayName!.Contains(search));
        var query = from d in devices
            join item in db.PublicTemplates.AsNoTracking().Where(t => !t.IsRetired) on d.PublicTemplateId equals (Guid?)item.Id into templates
            from t in templates.DefaultIfEmpty()
            where (d.PublicTemplateId == null && d.PublicPolicyVersion == PublicationPolicy.BaselineVersion) ||
                (t != null && d.PublicPolicyVersion == PublicationPolicy.TemplateVersion)
            orderby d.PublicDisplayName, d.RowId
            select new { Device = d, Template = t };
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        var items = rows.Select(x => MapDevice(x.Device, x.Template)).OfType<PublishedDevicePolicy>().ToArray();
        return new(items.Take(PageSize).ToArray(), page, rows.Length > PageSize);
    }

    public async Task<PublicPage<PublishedServicePolicy>> ServicesAsync(string? search, int page, Guid? groupId, CancellationToken token)
    {
        ValidatePage(page);
        if (groupId is { } requestedGroup && await GroupAsync(requestedGroup, token) is null) return new([], page, false);
        await using var db = await contexts.CreateDbContextAsync(token);
        var services = db.ApplicationServices.AsNoTracking().Where(s => s.IsPublic && s.ArchivedAt == null && s.PublicDisplayName != null &&
            (s.PublicPolicyVersion == PublicationPolicy.BaselineVersion || s.PublicPolicyVersion == PublicationPolicy.ServiceHistoryVersion ||
             s.PublicPolicyVersion == PublicationPolicy.ServiceHistoryDetailsVersion));
        if (groupId is { } group)
            services = services.Where(s => db.ServiceGroups.Any(m => m.ServiceId == s.Id && m.GroupId == group) &&
                db.Groups.Any(g => g.Id == group && g.IsPublic && g.PublicPolicyVersion == PublicationPolicy.BaselineVersion && g.PublicDisplayName != null));
        if (!string.IsNullOrWhiteSpace(search)) services = services.Where(s => s.PublicDisplayName!.Contains(search));
        var rows = await services.OrderBy(s => s.PublicDisplayName).ThenBy(s => s.RowId)
            .Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        var items = rows.Select(MapService).OfType<PublishedServicePolicy>().ToArray();
        return new(items.Take(PageSize).ToArray(), page, rows.Length > PageSize);
    }

    private static void ValidatePage(int page)
    {
        if (page is < 1 or > int.MaxValue / PageSize) throw new ArgumentOutOfRangeException(nameof(page));
    }

    public async Task<PublishedGroupPolicy?> GroupAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id && g.IsPublic, token);
        if (group is null || group.PublicPolicyVersion != PublicationPolicy.BaselineVersion ||
            !PublicationPolicy.TryValidateName(group.PublicDisplayName, out var name)) return null;
        return new(group.Id, name, Convert.ToBase64String(group.Version));
    }

    public async Task<PublishedDevicePolicy?> DeviceAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await (from d in db.Devices.AsNoTracking()
            join item in db.PublicTemplates.AsNoTracking().Where(t => !t.IsRetired) on d.PublicTemplateId equals (Guid?)item.Id into templates
            from t in templates.DefaultIfEmpty()
            where d.Id == id && d.IsPublic && !d.IsArchived
            select new { Device = d, Template = t }).SingleOrDefaultAsync(token);
        return row is null ? null : MapDevice(row.Device, row.Template);
    }

    private static PublishedDevicePolicy? MapDevice(Device device, PublicTemplate? template)
    {
        if (!PublicationPolicy.TryValidateName(device.PublicDisplayName, out var name)) return null;
        if (device.PublicPolicyVersion == PublicationPolicy.TemplateVersion && device.PublicTemplateId is { } templateId)
        {
            if (template is null || template.Id != templateId) return null;
        }
        else if (device.PublicPolicyVersion != PublicationPolicy.BaselineVersion || device.PublicTemplateId is not null) return null;
        return new(device.Id, name, device.Address, device.IsEnabled, template?.ShowLatency ?? true, template?.ShowObservedAt ?? true,
            device.PublicSince == Device.PendingPublicSince ? null : device.PublicSince, template?.HistoryDays ?? 0,
            template?.HistorySince == PublicTemplate.PendingHistorySince ? null : template?.HistorySince,
            Convert.ToBase64String(device.Version), template is null ? null : Convert.ToBase64String(template.Version));
    }

    public async Task<PublishedServicePolicy?> ServiceAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var service = await db.ApplicationServices.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.IsPublic && s.ArchivedAt == null, token);
        return service is null ? null : MapService(service);
    }

    private static PublishedServicePolicy? MapService(ApplicationService service)
    {
        if (service.PublicPolicyVersion is not (PublicationPolicy.BaselineVersion or PublicationPolicy.ServiceHistoryVersion or PublicationPolicy.ServiceHistoryDetailsVersion) ||
            !PublicationPolicy.TryValidateName(service.PublicDisplayName, out var name)) return null;
        return new(service.Id, name, service.PublicPolicyVersion,
            service.PublicSince == DateTimeOffset.MaxValue ? null : service.PublicSince, Convert.ToBase64String(service.Version));
    }
}
