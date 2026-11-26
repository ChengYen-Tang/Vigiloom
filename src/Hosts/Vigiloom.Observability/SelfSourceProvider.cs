using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Observability;

/// <summary>Creates one persistent source for trusted internal Vigiloom telemetry.</summary>
internal sealed class SelfSourceProvider(IDbContextFactory<ServiceObservabilityDbContext> contexts, TimeProvider clock)
{
    public async Task<AuthenticatedSource> GetAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var source = await db.Sources.FromSqlRaw("SELECT * FROM service.Sources WITH (UPDLOCK, HOLDLOCK) WHERE IsSelf = 1")
            .SingleOrDefaultAsync(token);
        if (source is null)
        {
            source = ServiceSource.CreateSelf(clock.GetUtcNow());
            db.Sources.Add(source);
            await db.SaveChangesAsync(token);
        }
        if (source.RevokedAt is not null) throw new InvalidOperationException("The internal telemetry source is revoked.");
        await transaction.CommitAsync(token);
        return new(source.Id, source.Version);
    }
}
