using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Vigiloom.Modules.IdentityAccess.Persistence;

public static class AccountMutationLock
{
    public static async Task<IDbContextTransaction> AcquireAsync(IdentityAccessDbContext db, CancellationToken token)
    {
        var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = 'Vigiloom.Identity.AccountMutation',
                    @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
                IF @result < 0 THROW 50000, 'Could not acquire account mutation lock.', 1;
                """, token);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
