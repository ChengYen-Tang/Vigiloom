using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Revalidates assessment evidence in the caller's transaction, retaining locks until commit.</summary>
internal static class LinuxAssessmentFence
{
    internal static async Task<bool> LockCurrentAsync(SecurityAnalysisDbContext db, IAgentOwnerFence owners, InventoryOwner owner,
        LinuxPackageAssessment assessment, TimeProvider clock, TimeSpan maximumAge, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Assessment publication requires a transaction.");
        if (transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("Assessment publication requires serializable isolation.");
        if (assessment.Status != LinuxEvaluationStatus.Ready || assessment.SourceId is not { } sourceId ||
            assessment.RevisionId is not { } revisionId || assessment.HostSnapshotId is not { } hostId ||
            assessment.PackageSnapshotId is not { } packageId || assessment.RunId is not { } runId || runId.Version != 7 ||
            assessment.SourceVersion is not { Length: 8 } || assessment.ActivationVersion is not { Length: 8 } ||
            assessment.Findings is null || string.IsNullOrWhiteSpace(assessment.Ecosystem) ||
            maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromDays(30)) return false;
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null || source.Key != (assessment.IsDebian ? DebianRevisionStage.SourceKey : UbuntuRevisionStage.SourceKey) || !source.Enabled || source.LastObservedRevisionId != revisionId ||
            !source.Version.AsSpan().SequenceEqual(assessment.SourceVersion) || source.LastSuccessfulCheckAt is not { } checkedAt ||
            checkedAt > now || checkedAt < now.AddSeconds(-source.StaleAfterSeconds)) return false;
        var revision = await db.IntelligenceRevisions.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceId] = {sourceId} AND [Id] = {revisionId}")
            .Select(x => new { x.Id, x.Revoked, x.Normalized }).SingleOrDefaultAsync(token);
        if (revision is null || revision.Revoked || !revision.Normalized) return false;
        var active = await db.IntelligenceActivations.SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (active is null || active.RevisionId != revisionId || !active.Version.AsSpan().SequenceEqual(assessment.ActivationVersion) ||
            !(assessment.IsDebian ? await DebianRevisionEligibility.IsEligibleAsync(db, sourceId, revisionId, token) :
                await UbuntuRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, now, token))) return false;
        var connection = db.Database.GetDbConnection();
        var sqlTransaction = transaction.GetDbTransaction();
        if (!await owners.LockCurrentAsync(connection, sqlTransaction, owner, token) ||
            !await InventoryAgentLock.TryAcquireAsync(connection, sqlTransaction, owner.AgentId, 5000, token)) return false;
        now = clock.GetUtcNow();
        if (checkedAt > now || checkedAt < now.AddSeconds(-source.StaleAfterSeconds)) return false;
        return await SnapshotCurrentAsync(InventorySource.HostSystem, hostId) && await SnapshotCurrentAsync(InventorySource.LinuxDpkg, packageId);

        async Task<bool> SnapshotCurrentAsync(InventorySource kind, Guid expected) => await (from state in db.InventorySources.AsNoTracking()
            join snapshot in db.InventorySnapshots.AsNoTracking() on new { state.AgentId, state.Source, Generation = state.CurrentGeneration }
                equals new { snapshot.AgentId, snapshot.Source, Generation = (long?)snapshot.Generation }
            where state.AgentId == owner.AgentId && state.DeviceId == owner.DeviceId && state.DeviceGeneration == owner.DeviceGeneration &&
                state.Source == kind && state.LastStatus == InventorySnapshotStatus.Complete && state.LastOutcome == InventoryOutcome.Complete &&
                snapshot.DeviceId == owner.DeviceId && snapshot.DeviceGeneration == owner.DeviceGeneration && snapshot.SnapshotId == expected &&
                snapshot.Status == InventorySnapshotStatus.Complete && snapshot.Outcome == InventoryOutcome.Complete &&
                snapshot.ObservedAt <= now.AddMinutes(5) && snapshot.ObservedAt >= now.Subtract(maximumAge)
            select snapshot.RowId).AnyAsync(token);
    }
}
