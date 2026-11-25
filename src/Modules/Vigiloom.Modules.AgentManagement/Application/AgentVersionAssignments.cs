using Vigiloom.UpdateSecurity.AgentPackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Maintains the desired package for one currently enrolled Agent.</summary>
public sealed class AgentVersionAssignments(IDbContextFactory<AgentManagementDbContext> contexts,
    IDeviceAgentSource devices, AgentIdentity identity, TimeProvider clock, IPackageLibraryTransport packages)
{
    public sealed record AgentRead(bool Authenticated, AgentVersionTarget? Target);

    /// <summary>Returns only the current assignment of the authenticated Agent.</summary>
    public async Task<AgentRead> GetForAgentAsync(string? credential, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return new(false, null);
        var row = await (from assignment in db.VersionAssignments.AsNoTracking()
            join package in db.Packages.FromSqlRaw("SELECT * FROM [agent].[Packages] WITH (HOLDLOCK)").AsNoTracking() on assignment.PackageId equals package.Id
            where assignment.AgentId == agent.Id && assignment.UpdateId != null
            select new { assignment.UpdateId, assignment.PackageId, PackageVersion = package.Version, package.RuntimeIdentifier, package.Sha256, package.ManifestSha256, Revision = assignment.Version })
            .SingleOrDefaultAsync(token);
        var target = row is null || !packages.IsCached(row.Sha256, row.ManifestSha256) ? null : new AgentVersionTarget(row.UpdateId!.Value, row.PackageId!.Value,
            row.PackageVersion, row.RuntimeIdentifier, row.ManifestSha256, Convert.ToBase64String(row.Revision));
        await transaction.CommitAsync(token);
        return new(true, target);
    }

    public async Task<AgentVersionAssignmentView?> GetAsync(Guid agentId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var agent = await db.Agents.AsNoTracking().Where(x => x.Id == agentId && x.RevokedAt == null)
            .Select(x => new { x.DeviceId, x.DeviceGeneration, x.RuntimeVersion, x.RuntimeIdentifier, x.RuntimeReportedAt }).SingleOrDefaultAsync(token);
        if (agent is null) return null;
        var device = await devices.GetAsync(agent.DeviceId, token);
        if (device is null || !device.Eligible || device.Generation != agent.DeviceGeneration) return null;
        var latest = await (from receipt in db.UpdateReceipts.AsNoTracking()
            join target in db.UpdateTargets.AsNoTracking() on receipt.UpdateId equals target.Id
            where receipt.AgentId == agentId && !db.UpdateReceipts.Any(other => other.UpdateId == receipt.UpdateId && other.Sequence > receipt.Sequence)
            orderby receipt.ReceivedAt descending, receipt.RowId descending
            select new AgentUpdateResultView(receipt.UpdateId, target.Version, receipt.Result,
                receipt.OccurredAt, receipt.ReceivedAt, receipt.RetryAllowed, receipt.Failure)).FirstOrDefaultAsync(token);
        var row = await (from assignment in db.VersionAssignments.AsNoTracking()
            join package in db.Packages.AsNoTracking() on assignment.PackageId equals package.Id into packages
            from package in packages.DefaultIfEmpty()
            where assignment.AgentId == agentId
            select new { assignment.AgentId, assignment.PackageId, PackageVersion = package == null ? null : package.Version,
                RuntimeIdentifier = package == null ? null : package.RuntimeIdentifier, Sha256 = package == null ? null : package.Sha256,
                ManifestSha256 = package == null ? null : package.ManifestSha256, assignment.AssignedAt, assignment.Version,
                Available = package != null }).SingleOrDefaultAsync(token);
        if (row is null) return new(agentId, null, null, null, default, "", false, agent.RuntimeVersion, agent.RuntimeIdentifier, agent.RuntimeReportedAt, latest);
        return new(row.AgentId, row.PackageId, row.PackageVersion, row.RuntimeIdentifier, row.AssignedAt,
            Convert.ToBase64String(row.Version), row.Available && row.Sha256 is not null && row.ManifestSha256 is not null &&
            packages.IsCached(row.Sha256, row.ManifestSha256), agent.RuntimeVersion, agent.RuntimeIdentifier, agent.RuntimeReportedAt, latest);
    }

    public async Task<VersionAssignmentResult> SetAsync(Guid agentId, Guid? packageId, string? revision, Guid actorId, CancellationToken token)
    {
        if (agentId == Guid.Empty || actorId == Guid.Empty || packageId == Guid.Empty) return VersionAssignmentResult.NotFound;
        byte[] expected;
        if (revision is null) return VersionAssignmentResult.Conflict;
        try { expected = Convert.FromBase64String(revision); }
        catch (FormatException) { return VersionAssignmentResult.Conflict; }
        if (expected.Length is not (0 or 8)) return VersionAssignmentResult.Conflict;

        await using var db = await contexts.CreateDbContextAsync(token);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentId && x.RevokedAt == null, token);
        if (agent is null) return VersionAssignmentResult.NotFound;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireAsync(db, agent.DeviceId, token);
        if (!await devices.IsCurrentAsync(agent.DeviceId, agent.DeviceGeneration, db.Database.GetDbConnection(), transaction.GetDbTransaction(), token) ||
            !await db.Agents.AnyAsync(x => x.Id == agentId && x.RevokedAt == null, token)) return VersionAssignmentResult.Conflict;
        var current = await db.VersionAssignments.SingleOrDefaultAsync(x => x.AgentId == agentId, token);
        if (current is null ? expected.Length != 0 : !expected.AsSpan().SequenceEqual(current.Version)) return VersionAssignmentResult.Conflict;
        var previousPackageId = current?.PackageId;
        if (packageId is { } selected)
        {
            var package = await db.Packages.FromSqlInterpolated($"SELECT * FROM [agent].[Packages] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {selected}")
                .SingleOrDefaultAsync(token);
            if (package is null ||
                !packages.IsCached(package.Sha256, package.ManifestSha256)) return VersionAssignmentResult.Conflict;
            if (current is null) db.VersionAssignments.Add(current = new AgentVersionAssignment { AgentId = agentId });
            current.PackageId = selected;
            current.UpdateId = Guid.CreateVersion7();
            current.AssignedBy = actorId;
            current.AssignedAt = clock.GetUtcNow().ToUniversalTime();
            db.UpdateTargets.Add(new AgentUpdateTarget
            {
                Id = current.UpdateId.Value, AgentId = agentId, PackageId = package.Id, Version = package.Version,
                RuntimeIdentifier = package.RuntimeIdentifier, ManifestSha256 = package.ManifestSha256, AssignedAt = current.AssignedAt
            });
        }
        else
        {
            if (current is null) return VersionAssignmentResult.Saved;
            if (current.PackageId is null) return VersionAssignmentResult.Saved;
            current.PackageId = null;
            current.UpdateId = null;
            current.AssignedBy = actorId;
            current.AssignedAt = clock.GetUtcNow().ToUniversalTime();
        }
        db.Audits.Add(new AgentAudit(agent.DeviceId, agent.Id, actorId, packageId is null ? "VersionAssignmentCancelled" : "VersionAssigned",
            clock.GetUtcNow(), previousPackageId, packageId));
        try { await db.SaveChangesAsync(token); await transaction.CommitAsync(token); }
        catch (DbUpdateConcurrencyException) { return VersionAssignmentResult.Conflict; }
        return VersionAssignmentResult.Saved;
    }
}
