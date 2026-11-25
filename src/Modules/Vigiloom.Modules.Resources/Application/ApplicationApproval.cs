using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vigiloom.Modules.Resources.Application;

public sealed class ApplicationApproval(IDbContextFactory<ResourcesDbContext> contexts, IApplicationSourceVerifier sources, IDbContextOutbox outbox)
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
        if (!await sources.IsActiveAsync(candidate.SourceId, token)) return new(ApplicationApprovalStatus.SourceUnavailable);
        var now = await ApplicationDiscovery.DatabaseTimeAsync(db, token);
        if (candidate.LastSeen > now || candidate.LastSeen < now.AddMinutes(-3)) return new(ApplicationApprovalStatus.Expired);
        var service = new ApplicationService(candidate, now);
        db.ApplicationServices.Add(service);
        db.ApplicationCandidates.Remove(candidate);
        await db.SaveChangesAsync(token);
        outbox.Enroll(db);
        var messageId = Guid.CreateVersion7().ToString("N");
        await outbox.PublishAsync(new ApplicationApproved(service.Id, service.SourceId, service.ServiceKey, service.Environment,
            service.DisplayName, service.ServiceNamespace, service.ApprovedAt, service.Version), new DeliveryOptions { DeduplicationId = messageId });
        await outbox.PublishAsync(new ApplicationApprovalAudit(actorId, service.Id, service.SourceId, now), new DeliveryOptions { DeduplicationId = messageId });
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        return new(ApplicationApprovalStatus.Approved, service.Id);
    }
}
