using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

internal static class DiscoveryTransaction
{
    // Approval, observation and capacity cleanup acquire this lock before source rows.
    public static async Task<bool> TryLockAsync(ServiceObservabilityDbContext db, IDbContextTransaction transaction, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'Vigiloom.ServiceCandidates', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) >= 0;
    }

    public static Task<DateTimeOffset> NowAsync(ServiceObservabilityDbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
}
