using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Registers known platform services through the trusted internal endpoint.</summary>
public sealed class SelfServiceRegistration(IDbContextFactory<ResourcesDbContext> contexts, IApplicationSourceVerifier sources, ResourceWork outbox)
{
    private static readonly HashSet<string> KnownServices = new(StringComparer.Ordinal)
    {
        "Vigiloom.Web", "Vigiloom.Worker", "Vigiloom.Gateway", "Vigiloom.Identity", "Vigiloom.Resources",
        "Vigiloom.Network", "Vigiloom.Host", "Vigiloom.Observability", "Vigiloom.Security", "Vigiloom.Operations", "Vigiloom.Messaging"
    };

    public async Task<Guid?> RegisterAsync(ApplicationObservation observation, CancellationToken token)
    {
        if (!KnownServices.Contains(observation.ServiceKey) || !(await sources.GetAsync(observation.SourceId, token)).Active)
            return null;

        var proposed = new ApplicationCandidate(observation.SourceId, observation.ServiceKey, observation.Environment,
            observation.DisplayName, observation.ServiceNamespace, DateTimeOffset.UnixEpoch);
        var namespaceBytes = Encoding.Unicode.GetBytes(proposed.ServiceNamespace);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await ApplicationDiscovery.LockAsync(db, transaction, token);
        var now = await ApplicationDiscovery.DatabaseTimeAsync(db, token);
        var service = await db.ApplicationServices.SingleOrDefaultAsync(s => s.SourceId == proposed.SourceId &&
            s.ServiceNamespaceBytes == namespaceBytes && s.ServiceKey == proposed.ServiceKey && s.Environment == proposed.Environment, token);
        if (service is { RevokedAt: not null } or { ArchivedAt: not null }) return null;
        var created = service is null;
        if (created)
        {
            service = new ApplicationService(proposed.SourceId, proposed.ServiceKey, proposed.Environment, proposed.DisplayName, proposed.ServiceNamespace, now);
            db.ApplicationServices.Add(service);
        }
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Name == "Self", token);
        if (group is null)
        {
            group = new Group("Self", now);
            db.Groups.Add(group);
        }
        if (!await db.ServiceGroups.AnyAsync(m => m.GroupId == group.Id && m.ServiceId == service!.Id, token))
            db.ServiceGroups.Add(new ServiceGroup(group.Id, service!.Id));
        var candidate = await db.ApplicationCandidates.SingleOrDefaultAsync(c => c.SourceId == proposed.SourceId &&
            c.ServiceNamespaceBytes == namespaceBytes && c.ServiceKey == proposed.ServiceKey && c.Environment == proposed.Environment, token);
        if (candidate is not null) db.ApplicationCandidates.Remove(candidate);
        await db.SaveChangesAsync(token);
        if (created)
        {

            await outbox.PublishAsync(db, new ApplicationApproved(service!.Id, service.SourceId, service.ServiceKey, service.Environment,
                service.DisplayName, service.ServiceNamespace, service.ApprovedAt, service.Version), token);
            await db.Database.CurrentTransaction!.CommitAsync(token);
        }
        else await transaction.CommitAsync(token);
        return service!.Id;
    }
}
