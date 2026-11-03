using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

/// <summary>Approves a live candidate and writes the corresponding audit in the same SQL transaction.</summary>
public sealed class ServiceApproval(IDbContextFactory<ServiceObservabilityDbContext> contexts, IOperationAuditWriter<OperationAudit> audit)
{
    public async Task<ApprovalResult> ApproveAsync(Guid candidateId, Guid actorId, CancellationToken token)
    {
        if (candidateId.Version != 7 || actorId == Guid.Empty) throw new ArgumentException("A candidate and authenticated administrator are required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await DiscoveryTransaction.TryLockAsync(db, transaction, token)) return new(ApprovalStatus.Busy);
        var existing = await db.Services.AsNoTracking().SingleOrDefaultAsync(x => x.CandidateId == candidateId, token);
        if (existing is not null) return new(ApprovalStatus.AlreadyApproved, existing.Id);
        var candidate = await db.Candidates.SingleOrDefaultAsync(x => x.Id == candidateId, token);
        if (candidate is null) return new(ApprovalStatus.NotFound);
        var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {candidate.SourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null || source.RevokedAt is not null) return new(ApprovalStatus.SourceUnavailable);
        var now = await DiscoveryTransaction.NowAsync(db, token);
        if (candidate.LastSeen > now || candidate.LastSeen < now.AddMinutes(-3)) return new(ApprovalStatus.Expired);
        var service = new MonitoredService(candidate, now);
        db.Services.Add(service);
        db.Candidates.Remove(candidate);
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actorId, service.Id, now, new ServiceApproved(source.Id)), token);
        await transaction.CommitAsync(token);
        return new(ApprovalStatus.Approved, service.Id);
    }
}

public enum ApprovalStatus { Approved, AlreadyApproved, NotFound, Expired, SourceUnavailable, Busy }
public sealed record ApprovalResult(ApprovalStatus Status, Guid? ServiceId = null);
