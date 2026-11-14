using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Serializes publication changes with edits to devices included in impact previews.</summary>
public static class PublicationMutationLock
{
    public static async Task<bool> TryAcquireAsync(ResourcesDbContext db, CancellationToken token)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("A transaction is required.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'Vigiloom.PublicationMutation', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) >= 0;
    }
}
