using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.Publishing;

internal enum PublicationChangeStatus { Updated, NotFound, Conflict, Invalid }
internal sealed record PublicationChangeResult(PublicationChangeStatus Status, PublicationSettings? Settings = null);

/// <summary>Coordinates owner-module publication settings and audit in one database transaction.</summary>
internal sealed class PublicationAdministration(IDbContextFactory<ResourcesDbContext> resources,
    IDbContextFactory<ServiceObservabilityDbContext> services, IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    public async Task<PublicationSettings?> ReadAsync(PublicationResourceKind kind, Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        if (kind == PublicationResourceKind.Service)
        {
            await using var db = await services.CreateDbContextAsync(token);
            var row = await db.Services.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            return row is null ? null : Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version);
        }
        await using var resourceDb = await resources.CreateDbContextAsync(token);
        if (kind == PublicationResourceKind.Device)
        {
            var row = await resourceDb.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            return row is null ? null : Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version);
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
        if (change.Public && (change.PolicyVersion != PublicationPolicy.BaselineVersion || !PublicationPolicy.TryValidateName(change.DisplayName, out name)))
            return new(PublicationChangeStatus.Invalid);
        if (kind == PublicationResourceKind.Service)
        {
            await using var db = await services.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var row = await db.Services.FromSqlInterpolated($"SELECT * FROM [service].[Services] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
            if (row is null) return new(PublicationChangeStatus.NotFound);
            if (!row.Version.AsSpan().SequenceEqual(version)) return new(PublicationChangeStatus.Conflict);
            var previous = row.IsPublic; var changedName = change.Public && row.PublicDisplayName != name;
            if (previous == change.Public && !changedName && (!change.Public || row.PublicPolicyVersion == change.PolicyVersion))
                return new(PublicationChangeStatus.Updated, Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version));
            var now = clock.GetUtcNow().ToUniversalTime();
            row.SetPublication(change.Public, name, change.PolicyVersion, now);
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateConcurrencyException) { return new(PublicationChangeStatus.Conflict); }
            await AppendAsync(db, tx, actor, id, now, kind, previous, change.Public, changedName, token);
            await tx.CommitAsync(token);
            return new(PublicationChangeStatus.Updated, Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version));
        }
        await using var resourcesDb = await resources.CreateDbContextAsync(token);
        await using var transaction = await resourcesDb.Database.BeginTransactionAsync(token);
        if (kind == PublicationResourceKind.Device)
        {
            var row = await resourcesDb.Devices.FromSqlInterpolated($"SELECT * FROM [resources].[Devices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
            if (row is null) return new(PublicationChangeStatus.NotFound);
            if (row.IsArchived || !row.Version.AsSpan().SequenceEqual(version)) return new(PublicationChangeStatus.Conflict);
            var previous = row.IsPublic; var changedName = change.Public && row.PublicDisplayName != name;
            if (previous == change.Public && !changedName && (!change.Public || row.PublicPolicyVersion == change.PolicyVersion))
                return new(PublicationChangeStatus.Updated, Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version));
            var now = clock.GetUtcNow().ToUniversalTime();
            row.SetPublication(change.Public, name, change.PolicyVersion, now);
            try { await resourcesDb.SaveChangesAsync(token); }
            catch (DbUpdateConcurrencyException) { return new(PublicationChangeStatus.Conflict); }
            await AppendAsync(resourcesDb, transaction, actor, id, now, kind, previous, change.Public, changedName, token);
            await transaction.CommitAsync(token);
            return new(PublicationChangeStatus.Updated, Settings(id, row.IsPublic, row.PublicDisplayName, row.PublicPolicyVersion, row.Version));
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
        PublicationResourceKind kind, bool previous, bool current, bool nameChanged, CancellationToken token) =>
        audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, id, now, new PublicationChanged(kind, previous, current, nameChanged)), token);

    private static PublicationSettings Settings(Guid id, bool isPublic, string? name, string? policy, byte[] version) =>
        new(id, isPublic, name, policy, Convert.ToBase64String(version));
    private static bool TryVersion(string? text, out byte[] version)
    {
        version = [];
        try { version = Convert.FromBase64String(text ?? ""); return version.Length == 8; }
        catch (FormatException) { return false; }
    }
}
