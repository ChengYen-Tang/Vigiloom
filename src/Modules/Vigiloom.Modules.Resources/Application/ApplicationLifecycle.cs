using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Owns application lifecycle and reliably announces changes to observation.</summary>
public sealed class ApplicationLifecycle(IDbContextFactory<ResourcesDbContext> contexts, IApplicationSourceVerifier sources,
    ResourceWork outbox)
{
    public async Task<ApplicationStateChangeStatus> ChangeAsync(Guid id, ApplicationStateChange change, CancellationToken token)
    {
        if (id.Version != 7 || change.ActorId == Guid.Empty || change.Action is not ("stop" or "resume" or "archive" or "restore"))
            return ApplicationStateChangeStatus.Conflict;
        byte[] expected;
        try { expected = Convert.FromBase64String(change.Version ?? ""); }
        catch (FormatException) { return ApplicationStateChangeStatus.Conflict; }
        if (expected.Length != 8) return ApplicationStateChangeStatus.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var service = await db.ApplicationServices.FromSqlInterpolated($"SELECT * FROM [resources].[ApplicationServices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
            .SingleOrDefaultAsync(token);
        if (service is null) return ApplicationStateChangeStatus.NotFound;
        if (!service.Version.AsSpan().SequenceEqual(expected)) return ApplicationStateChangeStatus.Conflict;
        if (change.Action == "stop" && (service.ArchivedAt is not null || service.RevokedAt is not null) ||
            change.Action == "resume" && (service.ArchivedAt is not null || service.RevokedAt is null) ||
            change.Action == "archive" && service.ArchivedAt is not null ||
            change.Action == "restore" && service.ArchivedAt is null) return ApplicationStateChangeStatus.Conflict;
        if (change.Action == "resume" && !(await sources.GetAsync(service.SourceId, token)).Active)
            return ApplicationStateChangeStatus.SourceUnavailable;
        var now = await ApplicationDiscovery.DatabaseTimeAsync(db, token);
        switch (change.Action)
        {
            case "stop": service.Stop(now); break;
            case "resume": service.Resume(now); break;
            case "archive": service.Archive(now); break;
            case "restore": service.Restore(now); break;
        }
        await db.SaveChangesAsync(token);

        await outbox.PublishAsync(db, new ApplicationStateChanged(id, service.RevokedAt, service.ArchivedAt, service.Version), token);
        await outbox.PublishAsync(db, new ApplicationStateAudit(change.ActorId, id, change.Action, now), token);
        await db.Database.CurrentTransaction!.CommitAsync(token);
        return ApplicationStateChangeStatus.Updated;
    }
}
