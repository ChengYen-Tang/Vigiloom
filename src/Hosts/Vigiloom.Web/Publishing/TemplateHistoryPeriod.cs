using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Confirms a conservative template history boundary after its settings transaction commits.</summary>
internal static class TemplateHistoryPeriod
{
    internal static async Task<byte[]?> ConfirmAsync(IDbContextFactory<ResourcesDbContext> resources, Guid id, byte[] expectedVersion, CancellationToken token)
    {
        try
        {
            await using var db = await resources.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (!await PublicationMutationLock.TryAcquireAsync(db, token)) return null;
            var options = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(db.Database.GetDbConnection()).Options;
            await using var templates = new PublishingDbContext(options);
            await templates.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
            var row = await templates.Templates.SingleOrDefaultAsync(x => x.Id == id && !x.IsRetired, token);
            if (row is null || row.HistorySince != PublicationTemplate.PendingHistorySince || !row.Version.AsSpan().SequenceEqual(expectedVersion)) return null;
            var afterCommit = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            row.ConfirmHistorySince(afterCommit);
            await templates.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return row.Version;
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (Exception ex) when (ex is DbException or DbUpdateException && !token.IsCancellationRequested) { return null; }
    }
}
