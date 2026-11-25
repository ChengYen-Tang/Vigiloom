using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vigiloom.Modules.Resources.Application.Publishing;

/// <summary>Owns the public/private settings of monitored resources.</summary>
public sealed class PublicationSettingsManager(IDbContextFactory<ResourcesDbContext> contexts, TimeProvider clock,
    IDbContextOutbox outbox, ILogger<PublicationSettingsManager> logger)
{
    public async Task<PublicationSettings?> GetAsync(PublicationResourceKind kind, Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (kind == PublicationResourceKind.Device)
        {
            var row = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, token);
            if (row is null) return null;
            var templateVersion = row.PublicTemplateId is { } tid ? await db.PublicTemplates.AsNoTracking().Where(t => t.Id == tid && !t.IsRetired).Select(t => t.Version).SingleOrDefaultAsync(token) : null;
            return Settings(row.Id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version, row.PublicTemplateId, templateVersion);
        }
        if (kind == PublicationResourceKind.Service)
        {
            var row = await db.ApplicationServices.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, token);
            return row is null ? null : Settings(row.Id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version,
                historyReady: row.IsPublic && row.PublicSince != DateTimeOffset.MaxValue);
        }
        if (kind == PublicationResourceKind.Group)
        {
            var row = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, token);
            return row is null ? null : Settings(row.Id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version);
        }
        return null;
    }

    public async Task<PublicationSaveResult> SetAsync(PublicationResourceKind kind, Guid id, PublicationChange change, Guid actor, CancellationToken token)
    {
        if (id.Version != 7 || actor == Guid.Empty || kind is < PublicationResourceKind.Device or > PublicationResourceKind.Group || !TryVersion(change.Version, out var expected))
            return new(PublicationSaveStatus.Invalid);
        var name = string.Empty;
        var templateSelected = change.Public && kind == PublicationResourceKind.Device && change.PolicyVersion == PublicationPolicy.TemplateVersion;
        byte[] expectedTemplate = [];
        if (templateSelected ? change.TemplateId?.Version != 7 || !TryVersion(change.TemplateVersion, out expectedTemplate)
            : change.TemplateId is not null || change.TemplateVersion is not null) return new(PublicationSaveStatus.Invalid);
        var serviceHistory = kind == PublicationResourceKind.Service && change.PolicyVersion is PublicationPolicy.ServiceHistoryVersion or PublicationPolicy.ServiceHistoryDetailsVersion;
        if (change.Public && ((!templateSelected && !serviceHistory && change.PolicyVersion != PublicationPolicy.BaselineVersion) ||
            !PublicationPolicy.TryValidateName(change.DisplayName, out name))) return new(PublicationSaveStatus.Invalid);

        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await PublicationMutationLock.TryAcquireAsync(db, token)) return new(PublicationSaveStatus.Conflict);
        var now = clock.GetUtcNow().ToUniversalTime();
        var previous = false;
        var changedName = false;
        var changedTemplate = false;
        var changedPolicy = false;
        Device? device = null;
        ApplicationService? service = null;
        if (kind == PublicationResourceKind.Device)
        {
            device = await db.Devices.SingleOrDefaultAsync(d => d.Id == id, token);
            if (device is null) return new(PublicationSaveStatus.NotFound);
            if (device.IsArchived || !device.Version.AsSpan().SequenceEqual(expected)) return new(PublicationSaveStatus.Conflict);
            var ids = new[] { device.PublicTemplateId, change.TemplateId }.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
            var active = await db.PublicTemplates.AsNoTracking().Where(t => ids.Contains(t.Id) && !t.IsRetired).ToArrayAsync(token);
            var selected = active.SingleOrDefault(t => t.Id == change.TemplateId);
            if (templateSelected && (selected is null || !selected.Version.AsSpan().SequenceEqual(expectedTemplate))) return new(PublicationSaveStatus.Conflict);
            previous = device.IsPublic;
            changedName = change.Public && device.PublicDisplayName != name;
            changedTemplate = change.Public && (device.PublicPolicyVersion != change.PolicyVersion || device.PublicTemplateId != change.TemplateId);
            if (previous == change.Public && !changedName && !changedTemplate)
            {
                await transaction.CommitAsync(token);
                if (device.PublicSince == Device.PendingPublicSince) await DevicePublicationPeriod.ConfirmAsync(contexts, id, device.Version, logger, token);
                return new(PublicationSaveStatus.Updated, await GetAsync(kind, id, token));
            }
            var wasEffective = previous && (device.PublicPolicyVersion == PublicationPolicy.BaselineVersion && device.PublicTemplateId is null ||
                device.PublicPolicyVersion == PublicationPolicy.TemplateVersion && active.Any(t => t.Id == device.PublicTemplateId));
            if (templateSelected) device.SetTemplatePublication(name, selected!.Id, !wasEffective || changedTemplate && selected.HistoryDays > 0, now);
            else device.SetPublication(change.Public, name, change.PolicyVersion, now, change.Public && !wasEffective);
        }
        else if (kind == PublicationResourceKind.Service)
        {
            service = await db.ApplicationServices.SingleOrDefaultAsync(s => s.Id == id, token);
            if (service is null) return new(PublicationSaveStatus.NotFound);
            if (service.ArchivedAt is not null || !service.Version.AsSpan().SequenceEqual(expected)) return new(PublicationSaveStatus.Conflict);
            previous = service.IsPublic;
            changedName = change.Public && service.PublicDisplayName != name;
            changedPolicy = previous && change.Public && service.PublicPolicyVersion != change.PolicyVersion;
            if (previous == change.Public && !changedName && (!change.Public || service.PublicPolicyVersion == change.PolicyVersion))
            {
                await transaction.CommitAsync(token);
                if (service.PublicSince == DateTimeOffset.MaxValue) await ConfirmServiceAsync(id, service.Version, token);
                return new(PublicationSaveStatus.Updated, await GetAsync(kind, id, token));
            }
            service.SetPublication(change.Public, name, change.PolicyVersion, now);
        }
        else
        {
            var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == id, token);
            if (group is null) return new(PublicationSaveStatus.NotFound);
            if (!group.Version.AsSpan().SequenceEqual(expected)) return new(PublicationSaveStatus.Conflict);
            previous = group.IsPublic;
            changedName = change.Public && group.PublicDisplayName != name;
            if (previous == change.Public && !changedName && (!change.Public || group.PublicPolicyVersion == change.PolicyVersion))
            { await transaction.CommitAsync(token); return new(PublicationSaveStatus.Updated, await GetAsync(kind, id, token)); }
            group.SetPublication(change.Public, name, change.PolicyVersion, now);
        }
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return new(PublicationSaveStatus.Conflict); }
        outbox.Enroll(db);
        await outbox.PublishAsync(new PublicationSettingAudit(actor, id, now, kind, previous, change.Public, changedName, changedTemplate, changedPolicy));
        if (service is not null) await outbox.PublishAsync(new ApplicationPublicationChanged(id, service.Version));
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        if (device?.PublicSince == Device.PendingPublicSince) await DevicePublicationPeriod.ConfirmAsync(contexts, id, device.Version, logger, token);
        if (service?.PublicSince == DateTimeOffset.MaxValue) await ConfirmServiceAsync(id, service.Version, token);
        return new(PublicationSaveStatus.Updated, await GetAsync(kind, id, token));
    }

    private async Task ConfirmServiceAsync(Guid id, byte[] version, CancellationToken token)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (!await PublicationMutationLock.TryAcquireAsync(db, token)) return;
            var row = await db.ApplicationServices.SingleOrDefaultAsync(s => s.Id == id, token);
            if (row is null || row.ArchivedAt is not null || !row.IsPublic || row.PublicSince != DateTimeOffset.MaxValue || !row.Version.AsSpan().SequenceEqual(version)) return;
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            row.ConfirmPublicSince(now);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateConcurrencyException) { }
        catch (Exception error) when (error is DbException or DbUpdateException && !token.IsCancellationRequested)
        { logger.LogWarning(error, "Unable to confirm the public history period for service {ServiceId}", id); }
    }

    private static PublicationSettings Settings(Guid id, bool isPublic, string? name, string? policy, byte[] version,
        Guid? templateId = null, byte[]? templateVersion = null, bool? historyReady = null) =>
        new(id, isPublic, name, policy, Convert.ToBase64String(version), templateId, templateVersion is null ? null : Convert.ToBase64String(templateVersion), historyReady);

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        try { version = Convert.FromBase64String(value ?? ""); return version.Length == 8; }
        catch (FormatException) { return false; }
    }
}
