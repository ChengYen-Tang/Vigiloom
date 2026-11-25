using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application.Publishing;

/// <summary>Reads current public authorization directly from the owning database.</summary>
public sealed class PublishedPolicies(IDbContextFactory<ResourcesDbContext> contexts)
{
    public async Task<PublishedDevicePolicy?> DeviceAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await (from d in db.Devices.AsNoTracking()
            join item in db.PublicTemplates.AsNoTracking().Where(t => !t.IsRetired) on d.PublicTemplateId equals (Guid?)item.Id into templates
            from t in templates.DefaultIfEmpty()
            where d.Id == id && d.IsPublic && !d.IsArchived
            select new { Device = d, Template = t }).SingleOrDefaultAsync(token);
        if (row is null || !PublicationPolicy.TryValidateName(row.Device.PublicDisplayName, out var name)) return null;
        var device = row.Device;
        var template = row.Template;
        if (device.PublicPolicyVersion == PublicationPolicy.TemplateVersion && device.PublicTemplateId is { } templateId)
        {
            if (template is null || template.Id != templateId) return null;
        }
        else if (device.PublicPolicyVersion != PublicationPolicy.BaselineVersion || device.PublicTemplateId is not null) return null;
        return new(device.Id, name, template?.ShowLatency ?? true, template?.ShowObservedAt ?? true,
            device.PublicSince == Device.PendingPublicSince ? null : device.PublicSince, template?.HistoryDays ?? 0,
            template?.HistorySince == PublicTemplate.PendingHistorySince ? null : template?.HistorySince,
            Convert.ToBase64String(device.Version), template is null ? null : Convert.ToBase64String(template.Version));
    }

    public async Task<PublishedServicePolicy?> ServiceAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var service = await db.ApplicationServices.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.IsPublic && s.ArchivedAt == null, token);
        if (service is null || service.PublicPolicyVersion is not (PublicationPolicy.BaselineVersion or PublicationPolicy.ServiceHistoryVersion or PublicationPolicy.ServiceHistoryDetailsVersion) ||
            !PublicationPolicy.TryValidateName(service.PublicDisplayName, out var name)) return null;
        return new(service.Id, name, service.PublicPolicyVersion,
            service.PublicSince == DateTimeOffset.MaxValue ? null : service.PublicSince, Convert.ToBase64String(service.Version));
    }
}
