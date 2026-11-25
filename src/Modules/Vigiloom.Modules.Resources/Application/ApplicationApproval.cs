using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public sealed class ApplicationApproval(IDbContextFactory<ResourcesDbContext> contexts, IApplicationSourceVerifier sources, ResourceWork outbox)
{
    public async Task<ApplicationApprovalResult> ApproveAsync(Guid candidateId, Guid actorId, CancellationToken token)
    {
        if (candidateId.Version != 7 || actorId == Guid.Empty) throw new ArgumentException("A candidate and administrator are required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await ApplicationDiscovery.LockAsync(db, transaction, token);
        var existing = await db.ApplicationServices.AsNoTracking().SingleOrDefaultAsync(x => x.CandidateId == candidateId, token);
        if (existing is not null)
            return existing.RevokedAt is null ? new(ApplicationApprovalStatus.AlreadyApproved, existing.Id) : new(ApplicationApprovalStatus.SourceUnavailable);
        var candidate = await db.ApplicationCandidates.SingleOrDefaultAsync(x => x.Id == candidateId, token);
        if (candidate is null) return new(ApplicationApprovalStatus.NotFound);
        if (!(await sources.GetAsync(candidate.SourceId, token)).Active) return new(ApplicationApprovalStatus.SourceUnavailable);
        var now = await ApplicationDiscovery.DatabaseTimeAsync(db, token);
        if (candidate.LastSeen > now || candidate.LastSeen < now.AddMinutes(-3)) return new(ApplicationApprovalStatus.Expired);
        var service = new ApplicationService(candidate, now);
        db.ApplicationServices.Add(service);
        db.ApplicationCandidates.Remove(candidate);
        await db.SaveChangesAsync(token);

        await outbox.PublishAsync(db, new ApplicationApproved(service.Id, service.SourceId, service.ServiceKey, service.Environment,
            service.DisplayName, service.ServiceNamespace, service.ApprovedAt, service.Version), token);
        await outbox.PublishAsync(db, new ApplicationApprovalAudit(actorId, service.Id, service.SourceId, now), token);
        await db.Database.CurrentTransaction!.CommitAsync(token);
        return new(ApplicationApprovalStatus.Approved, service.Id);
    }
}
