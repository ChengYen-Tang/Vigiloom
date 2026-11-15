using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Revalidates all Windows assessment inputs while the caller holds the publication transaction.</summary>
internal static class WindowsAssessmentFence
{
    internal static async Task<bool> LockCurrentAsync(SecurityAnalysisDbContext db, IDbContextFactory<SecurityAnalysisDbContext> contexts, IAgentOwnerFence owners, InventoryOwner owner,
        WindowsProductAssessment assessment, TimeProvider clock, TimeSpan maximumAge, CancellationToken token)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Assessment publication requires a transaction.");
        if (transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("Assessment publication requires serializable isolation.");
        if (assessment.Status != WindowsEvaluationStatus.Ready || assessment.HostSnapshotId is null || assessment.Evidence is null ||
            assessment.Cves is null || maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromDays(30)) return false;
        var lookup = new MsrcCandidateLookup(contexts, clock);
        if (!await lookup.IsCurrentAsync(db, assessment.Evidence, token) || assessment.Evidence.RevisionId is not { } revisionId ||
            !await MsrcRevisionEligibility.HasProductIndexAsync(db, revisionId, token)) return false;
        var connection = db.Database.GetDbConnection();
        var sqlTransaction = transaction.GetDbTransaction();
        if (!await owners.LockCurrentAsync(connection, sqlTransaction, owner, token) ||
            !await InventoryAgentLock.TryAcquireAsync(connection, sqlTransaction, owner.AgentId, 5000, token)) return false;
        var now = clock.GetUtcNow();
        if ((assessment.CatalogComplete is null) != (assessment.CatalogCheckedAt is null) ||
            assessment.CatalogCheckedAt > now || assessment.CatalogEvidence?.Any(x => x.ObservedAt > now || x.ObservedAt < now.AddDays(-1)) == true) return false;
        var host = await WindowsHostInventory.LoadAsync(db, owner, now, maximumAge, token);
        var quickFix = await WindowsPatchInventory.LoadAsync(db, owner, InventorySource.WindowsQuickFix, now, maximumAge, token);
        var updates = await WindowsPatchInventory.LoadAsync(db, owner, InventorySource.WindowsUpdate, now, maximumAge, token);
        if (host?.SnapshotId != assessment.HostSnapshotId || MsrcWindowsProduct.ProductName(host?.Host) != assessment.Product ||
            quickFix?.SnapshotId != assessment.QuickFix?.SnapshotId || updates?.SnapshotId != assessment.Updates?.SnapshotId ||
            !await MsrcRevisionEligibility.HasProductIndexAsync(db, revisionId, token) || !await lookup.IsCurrentAsync(db, assessment.Evidence, token)) return false;
        now = clock.GetUtcNow();
        return Fresh(host!.ObservedAt) && (quickFix is null || Fresh(quickFix.ObservedAt)) && (updates is null || Fresh(updates.ObservedAt)) &&
            (assessment.CatalogCheckedAt is null || assessment.CatalogCheckedAt <= now && assessment.CatalogCheckedAt >= now.AddDays(-1)) &&
            assessment.CatalogEvidence?.All(x => x.ObservedAt <= now && x.ObservedAt >= now.AddDays(-1)) != false;

        bool Fresh(DateTimeOffset observed) => observed <= now.AddMinutes(5) && observed >= now.Subtract(maximumAge);
    }

}
