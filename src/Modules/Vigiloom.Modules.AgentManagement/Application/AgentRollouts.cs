using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Creates fixed group snapshots and advances a bounded number of Agents at a time.</summary>
public sealed class AgentRollouts(IDbContextFactory<AgentManagementDbContext> contexts, IGroupAgentSource groups,
    IDeviceAgentSource devices, TimeProvider clock)
{
    public async Task<AgentRolloutCreation> CreateAsync(AgentRolloutInput input, Guid actor, CancellationToken token)
    {
        if (input.GroupId.Version != 7 || input.PackageId.Version != 7 || actor.Version != 7 ||
            input.PilotSize < 1 || input.BatchSize < 1) return new(AgentRolloutResult.Invalid);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var snapshot = await SnapshotAsync(db, transaction, input.GroupId, input.PackageId, token);
        if (snapshot.Result != AgentRolloutResult.Saved) return new(snapshot.Result);
        if (snapshot.Eligible.Count == 0) return new(AgentRolloutResult.Invalid, Excluded: snapshot.Excluded);
        var agentIds = snapshot.Eligible.Select(x => x.AgentId).ToArray();
        var revisions = await db.VersionAssignments.AsNoTracking().Where(x => agentIds.Contains(x.AgentId))
            .Select(x => new { x.AgentId, x.Version }).ToDictionaryAsync(x => x.AgentId, x => x.Version, token);
        var now = clock.GetUtcNow().ToUniversalTime();
        var rollout = new AgentRollout { Id = Guid.CreateVersion7(), GroupId = input.GroupId, PackageId = input.PackageId,
            CreatedBy = actor, CreatedAt = now, PilotSize = input.PilotSize, BatchSize = input.BatchSize, Status = AgentRolloutStatus.Running };
        db.Rollouts.Add(rollout);
        for (var index = 0; index < snapshot.Eligible.Count; index++)
        {
            var target = snapshot.Eligible[index];
            db.RolloutMembers.Add(new AgentRolloutMember { RolloutId = rollout.Id, AgentId = target.AgentId, DeviceId = target.Device.DeviceId, DeviceName = target.Device.Name,
                DeviceGeneration = target.Device.Generation, Position = index,
                Batch = index < input.PilotSize ? 0 : 1 + (index - input.PilotSize) / input.BatchSize,
                ExpectedAssignmentVersion = revisions.GetValueOrDefault(target.AgentId), Status = AgentRolloutMemberStatus.Pending });
        }
        db.RolloutAudits.Add(new AgentRolloutAudit { RolloutId = rollout.Id, ActorId = actor, OccurredAt = now, Action = AgentRolloutAction.Created });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(AgentRolloutResult.Saved, rollout.Id, snapshot.Eligible.Count, snapshot.Excluded);
    }

    public async Task<AgentRolloutPreview?> PreviewAsync(Guid groupId, Guid packageId, CancellationToken token)
    {
        if (groupId.Version != 7 || packageId.Version != 7) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var snapshot = await SnapshotAsync(db, transaction, groupId, packageId, token);
        return snapshot.Result == AgentRolloutResult.Saved
            ? new(snapshot.Eligible.Count, snapshot.Excluded, snapshot.RuntimeIdentifier!) : null;
    }

    private async Task<Snapshot> SnapshotAsync(AgentManagementDbContext db, IDbContextTransaction transaction,
        Guid groupId, Guid packageId, CancellationToken token)
    {
        var package = await db.Packages.FromSqlInterpolated($"SELECT * FROM [agent].[Packages] WITH (HOLDLOCK) WHERE [Id] = {packageId}")
            .AsNoTracking().SingleOrDefaultAsync(token);
        if (package is null || !package.Listed || package.Revoked || package.VerifiedUntil <= clock.GetUtcNow())
            return new(AgentRolloutResult.Conflict, [], 0, null);
        var group = await groups.ReadAsync(groupId, db.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
        if (group is null) return new(AgentRolloutResult.NotFound, [], 0, null);
        var deviceIds = group.Select(x => x.DeviceId).ToArray();
        var agents = await db.Agents.AsNoTracking().Where(x => deviceIds.Contains(x.DeviceId) && x.RevokedAt == null &&
                x.RuntimeIdentifier == package.RuntimeIdentifier)
            .Select(x => new { x.Id, x.DeviceId, x.DeviceGeneration }).ToArrayAsync(token);
        var byDevice = agents.ToDictionary(x => x.DeviceId);
        var eligible = group.Where(x => byDevice.TryGetValue(x.DeviceId, out var agent) && agent.DeviceGeneration == x.Generation)
            .Select(x => new SnapshotTarget(x, byDevice[x.DeviceId].Id)).ToArray();
        return new(AgentRolloutResult.Saved, eligible, group.Count - eligible.Length, package.RuntimeIdentifier);
    }

    private sealed record SnapshotTarget(GroupAgentDevice Device, Guid AgentId);
    private sealed record Snapshot(AgentRolloutResult Result, IReadOnlyList<SnapshotTarget> Eligible, int Excluded, string? RuntimeIdentifier);

    public async Task<AgentRolloutView?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var rollout = await db.Rollouts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (rollout is null) return null;
        var package = await db.Packages.AsNoTracking().Where(x => x.Id == rollout.PackageId)
            .Select(x => new { x.Version, x.RuntimeIdentifier }).SingleAsync(token);
        var members = await db.RolloutMembers.AsNoTracking().Where(x => x.RolloutId == id).OrderBy(x => x.Position)
            .Select(x => new AgentRolloutMemberView(x.AgentId, x.DeviceId, x.DeviceName, x.Batch, x.Position, x.UpdateId, x.Status, x.Problem)).ToArrayAsync(token);
        var history = await db.RolloutAudits.AsNoTracking().Where(x => x.RolloutId == id).OrderBy(x => x.RowId)
            .Select(x => new AgentRolloutAuditView(x.ActorId, x.OccurredAt, x.Action)).ToArrayAsync(token);
        return new(rollout.Id, rollout.GroupId, rollout.PackageId, package.Version, package.RuntimeIdentifier, rollout.CreatedAt, rollout.PilotSize, rollout.BatchSize,
            rollout.Status, Convert.ToBase64String(rollout.Version), members, history);
    }

    public async Task<IReadOnlyList<AgentRolloutSummary>> ListAsync(Guid groupId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await (from rollout in db.Rollouts.AsNoTracking()
            join package in db.Packages.AsNoTracking() on rollout.PackageId equals package.Id
            where rollout.GroupId == groupId
            orderby rollout.RowId descending
            select new AgentRolloutSummary(rollout.Id, rollout.PackageId, package.Version, package.RuntimeIdentifier, rollout.CreatedAt, rollout.Status))
            .Take(100).ToArrayAsync(token);
    }

    public async Task<AgentRolloutResult> ChangeAsync(Guid id, string revision, AgentRolloutAction action, Guid actor, CancellationToken token)
    {
        if (actor.Version != 7 || revision is null || action is not (AgentRolloutAction.Paused or AgentRolloutAction.Resumed or AgentRolloutAction.ResumeRemaining or AgentRolloutAction.Cancelled))
            return AgentRolloutResult.Invalid;
        byte[] expected;
        try { expected = Convert.FromBase64String(revision); }
        catch (FormatException) { return AgentRolloutResult.Invalid; }
        if (expected.Length != 8) return AgentRolloutResult.Invalid;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var rollout = await LockAsync(db, id, token);
        if (rollout is null) return AgentRolloutResult.NotFound;
        if (!rollout.Version.AsSpan().SequenceEqual(expected)) return AgentRolloutResult.Conflict;
        var hasIssues = await db.RolloutMembers.AnyAsync(x => x.RolloutId == id &&
            (x.Status == AgentRolloutMemberStatus.Failed || x.Status == AgentRolloutMemberStatus.Conflict), token);
        var next = action switch
        {
            AgentRolloutAction.Paused when rollout.Status == AgentRolloutStatus.Running => AgentRolloutStatus.Paused,
            AgentRolloutAction.Resumed when rollout.Status == AgentRolloutStatus.Paused && !hasIssues => AgentRolloutStatus.Running,
            AgentRolloutAction.ResumeRemaining when rollout.Status == AgentRolloutStatus.Paused && hasIssues => AgentRolloutStatus.Running,
            AgentRolloutAction.Cancelled when rollout.Status is AgentRolloutStatus.Running or AgentRolloutStatus.Paused => AgentRolloutStatus.Cancelled,
            _ => (AgentRolloutStatus?)null
        };
        if (next is null) return AgentRolloutResult.Conflict;
        rollout.Status = next.Value;
        db.RolloutAudits.Add(new AgentRolloutAudit { RolloutId = id, ActorId = actor, OccurredAt = clock.GetUtcNow().ToUniversalTime(), Action = action });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AgentRolloutResult.Saved;
    }

    /// <summary>Advances each current batch. Every member transition commits in its own transaction.</summary>
    public async Task AdvanceAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        long after = 0;
        while (true)
        {
            var rollouts = await db.Rollouts.AsNoTracking().Where(x => x.Status == AgentRolloutStatus.Running && x.RowId > after)
                .OrderBy(x => x.RowId).Select(x => new { x.Id, x.RowId }).Take(100).ToArrayAsync(token);
            if (rollouts.Length == 0) break;
            foreach (var row in rollouts)
            {
                var rolloutId = row.Id;
                for (var pass = 0; pass < 1000; pass++)
                {
                    var remaining = db.RolloutMembers.AsNoTracking().Where(x => x.RolloutId == rolloutId &&
                        (x.Status == AgentRolloutMemberStatus.Pending || x.Status == AgentRolloutMemberStatus.Assigned));
                    var batch = await remaining.Select(x => (int?)x.Batch).MinAsync(token);
                    if (batch is null) { await FinishAsync(rolloutId, token); break; }
                    var members = await remaining.Where(x => x.Batch == batch.Value)
                        .OrderByDescending(x => x.Status).ThenBy(x => x.Position).Select(x => x.AgentId).ToArrayAsync(token);
                    var changed = false;
                    foreach (var agentId in members) changed |= await AdvanceMemberAsync(rolloutId, agentId, token);
                    if (!changed) break;
                    if (!await db.Rollouts.AsNoTracking().AnyAsync(x => x.Id == rolloutId && x.Status == AgentRolloutStatus.Running, token)) break;
                }
            }
            after = rollouts[^1].RowId;
        }
    }

    private async Task<bool> AdvanceMemberAsync(Guid rolloutId, Guid agentId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var rollout = await LockAsync(db, rolloutId, token);
        if (rollout?.Status != AgentRolloutStatus.Running) return false;
        var member = await db.RolloutMembers.SingleOrDefaultAsync(x => x.RolloutId == rolloutId && x.AgentId == agentId, token);
        if (member is null || member.Status is not (AgentRolloutMemberStatus.Pending or AgentRolloutMemberStatus.Assigned)) return false;
        await AgentDeviceLock.AcquireAsync(db, member.DeviceId, token);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentId && x.RevokedAt == null, token);
        if (agent is null || agent.DeviceGeneration != member.DeviceGeneration ||
            !await devices.IsCurrentAsync(member.DeviceId, member.DeviceGeneration, db.Database.GetDbConnection(), transaction.GetDbTransaction(), token))
            return await PauseAsync(db, transaction, rollout, member, AgentRolloutProblem.AgentUnavailable, token);
        var current = await db.VersionAssignments.SingleOrDefaultAsync(x => x.AgentId == agentId, token);
        if (member.Status == AgentRolloutMemberStatus.Assigned)
        {
            if (current?.UpdateId != member.UpdateId)
                return await PauseAsync(db, transaction, rollout, member, AgentRolloutProblem.AssignmentChanged, token);
            var result = await db.UpdateReceipts.AsNoTracking().Where(x => x.UpdateId == member.UpdateId)
                .OrderByDescending(x => x.Sequence).Select(x => (AgentUpdateResultKind?)x.Result).FirstOrDefaultAsync(token);
            if (result == AgentUpdateResultKind.Succeeded)
            {
                member.Status = AgentRolloutMemberStatus.Succeeded;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return true;
            }
            if (result is AgentUpdateResultKind.RolledBack or AgentUpdateResultKind.RollbackFailed)
                return await PauseAsync(db, transaction, rollout, member, AgentRolloutProblem.UpdateFailed, token);
        }
        else if (current is null ? member.ExpectedAssignmentVersion is not null :
            member.ExpectedAssignmentVersion is null || !current.Version.AsSpan().SequenceEqual(member.ExpectedAssignmentVersion))
            return await PauseAsync(db, transaction, rollout, member, AgentRolloutProblem.AssignmentChanged, token);

        if (member.Status == AgentRolloutMemberStatus.Pending && current?.UpdateId is { } previous &&
            await IsOutstandingAsync(db, previous, token)) return false;

        var package = await db.Packages.FromSqlInterpolated($"SELECT * FROM [agent].[Packages] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {rollout.PackageId}")
            .SingleOrDefaultAsync(token);
        if (package is null || !package.Listed || package.Revoked || package.VerifiedUntil <= clock.GetUtcNow())
            return await PauseAsync(db, transaction, rollout, member, AgentRolloutProblem.PackageUnavailable, token);
        if (member.Status == AgentRolloutMemberStatus.Assigned) return false;
        member.UpdateId = AgentVersionAssignments.Assign(db, agent, current, package, rollout.CreatedBy, clock.GetUtcNow());
        member.Status = AgentRolloutMemberStatus.Assigned;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    private async Task<bool> PauseAsync(AgentManagementDbContext db, IDbContextTransaction transaction, AgentRollout rollout,
        AgentRolloutMember member, AgentRolloutProblem problem, CancellationToken token)
    {
        member.Status = problem == AgentRolloutProblem.UpdateFailed ? AgentRolloutMemberStatus.Failed : AgentRolloutMemberStatus.Conflict;
        member.Problem = problem;
        rollout.Status = AgentRolloutStatus.Paused;
        db.RolloutAudits.Add(new AgentRolloutAudit { RolloutId = rollout.Id, OccurredAt = clock.GetUtcNow().ToUniversalTime(), Action = AgentRolloutAction.AutomaticallyPaused });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    private async Task FinishAsync(Guid rolloutId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var rollout = await LockAsync(db, rolloutId, token);
        if (rollout?.Status != AgentRolloutStatus.Running || await db.RolloutMembers.AnyAsync(x => x.RolloutId == rolloutId &&
            (x.Status == AgentRolloutMemberStatus.Pending || x.Status == AgentRolloutMemberStatus.Assigned), token)) return;
        rollout.Status = await db.RolloutMembers.AnyAsync(x => x.RolloutId == rolloutId &&
            (x.Status == AgentRolloutMemberStatus.Failed || x.Status == AgentRolloutMemberStatus.Conflict), token)
            ? AgentRolloutStatus.CompletedWithIssues : AgentRolloutStatus.Completed;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private static Task<AgentRollout?> LockAsync(AgentManagementDbContext db, Guid id, CancellationToken token) =>
        db.Rollouts.FromSqlInterpolated($"SELECT * FROM [agent].[Rollouts] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);

    private static async Task<bool> IsOutstandingAsync(AgentManagementDbContext db, Guid updateId, CancellationToken token)
    {
        var last = await db.UpdateReceipts.AsNoTracking().Where(x => x.UpdateId == updateId)
            .OrderByDescending(x => x.Sequence).Select(x => new { x.Result, x.RetryAllowed }).FirstOrDefaultAsync(token);
        return last is null || last.Result is not (AgentUpdateResultKind.Succeeded or AgentUpdateResultKind.RollbackFailed) &&
            (last.Result != AgentUpdateResultKind.RolledBack || last.RetryAllowed);
    }
}
