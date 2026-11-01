using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Modules.IdentityAccess.Persistence;

/// <summary>Creates the built-in roles once, without replacing their identifiers on restart or retry.</summary>
public static class RoleInitializer
{
    public static async Task InitializeAsync(IDbContextFactory<IdentityAccessDbContext> factory, CancellationToken cancellationToken = default)
    {
        // Allocate once outside the execution strategy so a retry never changes a proposed identifier.
        var administratorId = ResourceIds.Create();
        var readOnlyId = ResourceIds.Create();
        await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Serialize initializers across hosts; release on commit or rollback.
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = 'Vigiloom.Identity.BuiltInRoles',
                    @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
                IF @result < 0 THROW 50000, 'Could not acquire built-in role initialization lock.', 1;
                """, cancellationToken);
            var existing = await db.Roles.Select(role => role.NormalizedName).ToListAsync(cancellationToken);
            if (!existing.Contains("ADMINISTRATOR")) db.Roles.Add(new Role("Administrator", administratorId));
            if (!existing.Contains("READ ONLY")) db.Roles.Add(new Role("Read only", readOnlyId));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
