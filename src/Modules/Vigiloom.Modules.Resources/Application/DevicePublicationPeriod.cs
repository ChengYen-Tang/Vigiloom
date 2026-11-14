using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Confirms a conservative history boundary after a device publication commits.</summary>
public static class DevicePublicationPeriod
{
    public static async Task<byte[]?> ConfirmAsync(IDbContextFactory<ResourcesDbContext> contexts, Guid id, byte[] publishedVersion,
        ILogger? logger, CancellationToken token)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            if (!await PublicationMutationLock.TryAcquireAsync(db, token)) return null;
            var row = await db.Devices.SingleOrDefaultAsync(x => x.Id == id, token);
            if (row is null || row.IsArchived || !row.IsPublic || row.PublicSince != Device.PendingPublicSince ||
                !row.Version.AsSpan().SequenceEqual(publishedVersion)) return null;
            var afterCommit = await db.Database.SqlQueryRaw<DateTimeOffset>(
                "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            row.ConfirmPublicSince(afterCommit);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return row.Version;
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (Exception ex) when (ex is DbException or DbUpdateException && !token.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "Unable to confirm the public history period for device {DeviceId}", id);
            return null;
        }
    }
}
