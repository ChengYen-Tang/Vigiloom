using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.UpdateSecurity.AgentPackages;

using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Modules.Resources.Application.AgentPackages;

/// <summary>Owns desired Agent versions and publishes changes for delivery to the Agent.</summary>
public sealed class AgentAssignments(IDbContextFactory<ResourcesDbContext> contexts, IPackageLibraryTransport files,
    ResourceWork outbox, TimeProvider clock, IHostAgentVersion? host = null)
{
    public async Task<AgentVersionAssignmentView?> GetAsync(Guid agentId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await EligibleAgents(db).AnyAsync(a => a.Id == agentId, token)) return null;
        var row = await (from assignment in db.AgentVersionAssignments.AsNoTracking()
            join package in db.AgentPackages.AsNoTracking() on assignment.PackageId equals package.Id into packages
            from package in packages.DefaultIfEmpty()
            where assignment.AgentId == agentId
            select new { assignment.PackageId, assignment.AssignedAt, assignment.Version, Package = package }).SingleOrDefaultAsync(token);
        var observation = host is null ? null : await host.GetAsync(agentId, token);
        if (row is null) return new(agentId, null, null, null, default, "", false,
            observation?.ReportedVersion, observation?.ReportedRuntimeIdentifier, observation?.ReportedAt, observation?.LatestResult);
        return new(agentId, row.PackageId, row.Package?.Version, row.Package?.RuntimeIdentifier, row.AssignedAt,
            Convert.ToBase64String(row.Version), row.Package is not null && files.IsCached(row.Package.Sha256, row.Package.ManifestSha256),
            observation?.ReportedVersion, observation?.ReportedRuntimeIdentifier, observation?.ReportedAt, observation?.LatestResult);
    }

    public async Task<AgentVersionTarget?> GetTargetAsync(Guid agentId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await (from agent in EligibleAgents(db)
            join assignment in db.AgentVersionAssignments.AsNoTracking() on agent.Id equals assignment.AgentId
            join package in db.AgentPackages.AsNoTracking() on assignment.PackageId equals package.Id
            where agent.Id == agentId && assignment.UpdateId != null
            select new { assignment.UpdateId, assignment.Version, Package = package }).SingleOrDefaultAsync(token);
        return row is null || !files.IsCached(row.Package.Sha256, row.Package.ManifestSha256) ? null :
            new AgentVersionTarget(row.UpdateId!.Value, row.Package.Id, row.Package.Version, row.Package.RuntimeIdentifier,
                row.Package.ManifestSha256, Convert.ToBase64String(row.Version));
    }

    public async Task<RecordedAgentUpdateTarget?> GetRecordedTargetAsync(Guid agentId, Guid updateId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await (from agent in EligibleAgents(db)
            join target in db.AgentUpdateTargets.AsNoTracking() on agent.Id equals target.AgentId
            where agent.Id == agentId && target.Id == updateId
            select new RecordedAgentUpdateTarget(target.Id, target.AgentId, target.PackageId, target.Version,
                target.RuntimeIdentifier, target.ManifestSha256, target.AssignedAt)).SingleOrDefaultAsync(token);
    }

    public async Task<VersionAssignmentResult> SetAsync(Guid agentId, AgentVersionAssignmentCommand request, CancellationToken token)
    {
        if (agentId == Guid.Empty || request.ActorId == Guid.Empty || request.PackageId == Guid.Empty) return VersionAssignmentResult.NotFound;
        byte[] expected;
        try { expected = Convert.FromBase64String(request.Revision); }
        catch (Exception error) when (error is FormatException or ArgumentNullException) { return VersionAssignmentResult.Conflict; }
        if (expected.Length is not (0 or 8)) return VersionAssignmentResult.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId && a.RevokedAt == null, token);
        if (agent is null) return VersionAssignmentResult.NotFound;
        var device = await db.Devices.FromSqlInterpolated($"SELECT * FROM [resources].[Devices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {agent.DeviceId}")
            .SingleOrDefaultAsync(token);
        var currentAgent = await db.Agents.FromSqlInterpolated($"SELECT * FROM [resources].[Agents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {agentId}")
            .AsNoTracking().SingleOrDefaultAsync(token);
        if (device is null || device.IsArchived || !device.IsEnabled || !device.DeepMonitoring || currentAgent is null ||
            currentAgent.RevokedAt is not null || currentAgent.DeviceId != device.Id || device.AgentAuthorizationGeneration != currentAgent.DeviceGeneration)
            return VersionAssignmentResult.Conflict;
        var assignment = await db.AgentVersionAssignments.SingleOrDefaultAsync(a => a.AgentId == agentId, token);
        if (assignment is null ? expected.Length != 0 : !expected.AsSpan().SequenceEqual(assignment.Version)) return VersionAssignmentResult.Conflict;
        var previousPackageId = assignment?.PackageId;
        if (request.PackageId is { } packageId)
        {
            var package = await db.AgentPackages.FromSqlInterpolated($"SELECT * FROM [resources].[AgentPackages] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {packageId}")
                .SingleOrDefaultAsync(token);
            if (package is null || !files.IsCached(package.Sha256, package.ManifestSha256)) return VersionAssignmentResult.Conflict;
            if (assignment is null) db.AgentVersionAssignments.Add(assignment = new AgentVersionAssignment { AgentId = agentId });
            assignment.PackageId = package.Id;
            assignment.UpdateId = Guid.CreateVersion7();
            assignment.AssignedBy = request.ActorId;
            assignment.AssignedAt = clock.GetUtcNow();
            db.AgentUpdateTargets.Add(new AgentUpdateTarget { Id = assignment.UpdateId.Value, AgentId = agentId, PackageId = package.Id,
                Version = package.Version, RuntimeIdentifier = package.RuntimeIdentifier, ManifestSha256 = package.ManifestSha256, AssignedAt = assignment.AssignedAt });
        }
        else
        {
            if (assignment?.PackageId is null) return VersionAssignmentResult.Saved;
            assignment.PackageId = null;
            assignment.UpdateId = null;
            assignment.AssignedBy = request.ActorId;
            assignment.AssignedAt = clock.GetUtcNow();
        }
        try
        {
            await db.SaveChangesAsync(token);

            await outbox.PublishAsync(db, new AgentAssignmentChanged(agentId, assignment.UpdateId), token);
            await outbox.PublishAsync(db, new AgentVersionAssignmentAudit(device.Id, agentId, request.ActorId,
                previousPackageId, assignment.PackageId, assignment.AssignedAt), token);
            await db.Database.CurrentTransaction!.CommitAsync(token);
            return VersionAssignmentResult.Saved;
        }
        catch (DbUpdateConcurrencyException) { return VersionAssignmentResult.Conflict; }
    }

    private static IQueryable<RegisteredAgent> EligibleAgents(ResourcesDbContext db) =>
        from agent in db.Agents.AsNoTracking()
        join device in db.Devices.AsNoTracking() on agent.DeviceId equals device.Id
        where agent.RevokedAt == null && !device.IsArchived && device.IsEnabled && device.DeepMonitoring &&
            device.AgentAuthorizationGeneration == agent.DeviceGeneration
        select agent;
}
