using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;

namespace Vigiloom.Web.Publishing;

internal enum PublicationChangeStatus { Updated, NotFound, Conflict, Invalid }
internal sealed record PublicationChangeResult(PublicationChangeStatus Status, PublicationSettings? Settings = null);

/// <summary>Coordinates owner-module publication settings and audit in one database transaction.</summary>
internal sealed class PublicationAdministration(IDbContextFactory<ResourcesDbContext> resources,
    IDbContextFactory<ServiceObservabilityDbContext> services, IDbContextFactory<PublishingDbContext> publishing,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock, ILogger<PublicationAdministration> logger)
{
    public async Task<PublicationSettings?> ReadAsync(PublicationResourceKind kind, Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        if (kind == PublicationResourceKind.Service)
        {
            await using var db = await services.CreateDbContextAsync(token);
            var row = await db.Services.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            return row is null ? null : Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version,
                historyReady: row.IsPublic && row.PublicSince != MonitoredService.PendingPublicSince);
        }
        await using var resourceDb = await resources.CreateDbContextAsync(token);
        if (kind == PublicationResourceKind.Device)
        {
            var row = await resourceDb.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            if (row is null) return null;
            byte[]? templateVersion = null;
            if (row.PublicTemplateId is { } templateId)
            {
                try
                {
                    await using var templateDb = await publishing.CreateDbContextAsync(token);
                    templateVersion = await templateDb.Templates.AsNoTracking().Where(x => x.Id == templateId && !x.IsRetired).Select(x => x.Version).SingleOrDefaultAsync(token);
                }
                catch (DbException ex) when (!token.IsCancellationRequested) { logger.LogWarning(ex, "Unable to read the device publication template version"); }
            }
            return Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version, row.PublicTemplateId, templateVersion);
        }
        if (kind == PublicationResourceKind.Group)
        {
            var row = await resourceDb.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            return row is null ? null : Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version);
        }
        return null;
    }

    public async Task<PublicationChangeResult> SetAsync(PublicationResourceKind kind, Guid id, PublicationChange change, Guid actor, CancellationToken token)
    {
        if (id.Version != 7 || actor == Guid.Empty || kind is < PublicationResourceKind.Device or > PublicationResourceKind.Group || !TryVersion(change.Version, out var version))
            return new(PublicationChangeStatus.Invalid);
        var name = string.Empty;
        var templateSelection = change.Public && kind == PublicationResourceKind.Device && change.PolicyVersion == PublicationPolicy.TemplateVersion;
        byte[] requestedTemplateVersion = [];
        if (templateSelection && (change.TemplateId?.Version != 7 || !TryVersion(change.TemplateVersion, out requestedTemplateVersion))) return new(PublicationChangeStatus.Invalid);
        if (!templateSelection && (change.TemplateId is not null || change.TemplateVersion is not null)) return new(PublicationChangeStatus.Invalid);
        var serviceHistory = kind == PublicationResourceKind.Service && change.PolicyVersion == PublicationPolicy.ServiceHistoryVersion;
        if (change.Public && ((!templateSelection && !serviceHistory && change.PolicyVersion != PublicationPolicy.BaselineVersion) || !PublicationPolicy.TryValidateName(change.DisplayName, out name)))
            return new(PublicationChangeStatus.Invalid);
        if (kind == PublicationResourceKind.Service)
        {
            await using var db = await services.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var row = await db.Services.FromSqlInterpolated($"SELECT * FROM [service].[Services] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
            if (row is null) return new(PublicationChangeStatus.NotFound);
            if (!row.Version.AsSpan().SequenceEqual(version)) return new(PublicationChangeStatus.Conflict);
            var previous = row.IsPublic; var changedName = change.Public && row.PublicDisplayName != name;
            var changedPolicy = previous && change.Public && row.PublicPolicyVersion != change.PolicyVersion;
            if (previous == change.Public && !changedName && (!change.Public || row.PublicPolicyVersion == change.PolicyVersion))
            {
                await tx.CommitAsync(token);
                var confirmed = row.PublicSince == MonitoredService.PendingPublicSince
                    ? await ConfirmServicePeriodAsync(id, row.Version, token) : null;
                return new(PublicationChangeStatus.Updated, Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, confirmed ?? row.Version,
                    historyReady: row.IsPublic && (confirmed is not null || row.PublicSince != MonitoredService.PendingPublicSince)));
            }
            var now = clock.GetUtcNow().ToUniversalTime();
            row.SetPublication(change.Public, name, change.PolicyVersion, now);
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateConcurrencyException) { return new(PublicationChangeStatus.Conflict); }
            await AppendAsync(db, tx, actor, id, now, kind, previous, change.Public, changedName, token, policyChanged: changedPolicy);
            await tx.CommitAsync(token);
            var confirmedVersion = row.PublicSince == MonitoredService.PendingPublicSince
                ? await ConfirmServicePeriodAsync(id, row.Version, token) : null;
            return new(PublicationChangeStatus.Updated, Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, confirmedVersion ?? row.Version,
                historyReady: row.IsPublic && (confirmedVersion is not null || row.PublicSince != MonitoredService.PendingPublicSince)));
        }
        await using var resourcesDb = await resources.CreateDbContextAsync(token);
        await using var transaction = await resourcesDb.Database.BeginTransactionAsync(token);
        if (kind == PublicationResourceKind.Device)
        {
            if (!await PublicationMutationLock.TryAcquireAsync(resourcesDb, token)) return new(PublicationChangeStatus.Conflict);
            var row = await resourcesDb.Devices.FromSqlInterpolated($"SELECT * FROM [resources].[Devices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
            if (row is null) return new(PublicationChangeStatus.NotFound);
            if (row.IsArchived || !row.Version.AsSpan().SequenceEqual(version)) return new(PublicationChangeStatus.Conflict);
            Dictionary<Guid, byte[]> templates = [];
            if (change.Public)
            {
                var options = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(resourcesDb.Database.GetDbConnection()).Options;
                await using var templateDb = new PublishingDbContext(options);
                await templateDb.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
                var ids = new[] { row.PublicTemplateId, change.TemplateId }.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
                templates = await templateDb.Templates.AsNoTracking().Where(x => ids.Contains(x.Id) && !x.IsRetired)
                    .Select(x => new { x.Id, x.Version }).ToDictionaryAsync(x => x.Id, x => x.Version, token);
            }
            if (templateSelection && (!templates.TryGetValue(change.TemplateId!.Value, out var selectedVersion) || !selectedVersion.AsSpan().SequenceEqual(requestedTemplateVersion)))
                return new(PublicationChangeStatus.Conflict);
            var previous = row.IsPublic; var changedName = change.Public && row.PublicDisplayName != name;
            var changedTemplate = change.Public && (row.PublicPolicyVersion != change.PolicyVersion || row.PublicTemplateId != change.TemplateId);
            if (previous == change.Public && !changedName && !changedTemplate)
            {
                await transaction.CommitAsync(token);
                var confirmed = row.PublicSince == Vigiloom.Modules.Resources.Domain.Device.PendingPublicSince
                    ? await DevicePublicationPeriod.ConfirmAsync(resources, id, row.Version, logger, token) : null;
                return new(PublicationChangeStatus.Updated, Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, confirmed ?? row.Version,
                    row.PublicTemplateId, row.PublicTemplateId is { } oldId && templates.TryGetValue(oldId, out var oldVersion) ? oldVersion : null));
            }
            var now = clock.GetUtcNow().ToUniversalTime();
            var wasEffective = previous && (row.PublicPolicyVersion == PublicationPolicy.BaselineVersion && row.PublicTemplateId is null ||
                row.PublicPolicyVersion == PublicationPolicy.TemplateVersion && row.PublicTemplateId is { } priorId && templates.ContainsKey(priorId));
            if (templateSelection) row.SetTemplatePublication(name, change.TemplateId!.Value, !wasEffective, now);
            else row.SetPublication(change.Public, name, change.PolicyVersion, now, restartPublicPeriod: change.Public && !wasEffective);
            try { await resourcesDb.SaveChangesAsync(token); }
            catch (DbUpdateConcurrencyException) { return new(PublicationChangeStatus.Conflict); }
            await AppendAsync(resourcesDb, transaction, actor, id, now, kind, previous, change.Public, changedName, token, changedTemplate);
            await transaction.CommitAsync(token);
            var confirmedVersion = row.PublicSince == Vigiloom.Modules.Resources.Domain.Device.PendingPublicSince
                ? await DevicePublicationPeriod.ConfirmAsync(resources, id, row.Version, logger, token) : null;
            return new(PublicationChangeStatus.Updated, Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, confirmedVersion ?? row.Version,
                row.PublicTemplateId, row.PublicTemplateId is { } currentId && templates.TryGetValue(currentId, out var currentVersion) ? currentVersion : null));
        }
        var group = await resourcesDb.Groups.FromSqlInterpolated($"SELECT * FROM [resources].[Groups] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
        if (group is null) return new(PublicationChangeStatus.NotFound);
        if (!group.Version.AsSpan().SequenceEqual(version)) return new(PublicationChangeStatus.Conflict);
        var wasPublic = group.IsPublic; var nameChanged = change.Public && group.PublicDisplayName != name;
        if (wasPublic == change.Public && !nameChanged && (!change.Public || group.PublicPolicyVersion == change.PolicyVersion))
            return new(PublicationChangeStatus.Updated, Settings(id, group.IsPublic, group.PublicDisplayName, group.PublicPolicyVersion, group.Version));
        var when = clock.GetUtcNow().ToUniversalTime();
        group.SetPublication(change.Public, name, change.PolicyVersion, when);
        try { await resourcesDb.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return new(PublicationChangeStatus.Conflict); }
        await AppendAsync(resourcesDb, transaction, actor, id, when, kind, wasPublic, change.Public, nameChanged, token);
        await transaction.CommitAsync(token);
        return new(PublicationChangeStatus.Updated, Settings(id, group.IsPublic, group.PublicDisplayName, group.PublicPolicyVersion, group.Version));
    }

    private Task AppendAsync(DbContext db, IDbContextTransaction tx, Guid actor, Guid id, DateTimeOffset now,
        PublicationResourceKind kind, bool previous, bool current, bool nameChanged, CancellationToken token, bool templateChanged = false, bool policyChanged = false) =>
        audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, id, now, new PublicationChanged(kind, previous, current, nameChanged, templateChanged, policyChanged)), token);

    private async Task<byte[]?> ConfirmServicePeriodAsync(Guid id, byte[] publishedVersion, CancellationToken token)
    {
        try
        {
            await using var db = await services.CreateDbContextAsync(token);
            var row = await db.Services.SingleOrDefaultAsync(x => x.Id == id, token);
            if (row is null || !row.IsPublic || row.PublicSince != MonitoredService.PendingPublicSince ||
                !row.Version.AsSpan().SequenceEqual(publishedVersion)) return null;
            // This time is obtained after the publication transaction committed. The rowversion
            // ensures a delayed confirmation cannot authorize history for a later publication.
            var afterCommit = await db.Database.SqlQueryRaw<DateTimeOffset>(
                "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            row.ConfirmPublicSince(afterCommit);
            await db.SaveChangesAsync(token);
            return row.Version;
        }
        catch (DbUpdateConcurrencyException) { /* A newer publication owns the period. */ }
        catch (Exception ex) when (ex is DbException or DbUpdateException && !token.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Unable to confirm the public history period for service {ServiceId}", id);
        }
        return null;
    }

    private static PublicationSettings Settings(Guid id, bool isPublic, string? name, string? policy, byte[] version, Guid? templateId = null, byte[]? templateVersion = null, bool? historyReady = null) =>
        new(id, isPublic, name, policy, Convert.ToBase64String(version), templateId, templateVersion is null ? null : Convert.ToBase64String(templateVersion), historyReady);
    private static bool TryVersion(string? text, out byte[] version)
    {
        version = [];
        try { version = Convert.FromBase64String(text ?? ""); return version.Length == 8; }
        catch (FormatException) { return false; }
    }
}
