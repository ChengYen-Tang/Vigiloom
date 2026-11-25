using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Events;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Timeline;

/// <summary>Stores a source event summary in the caller's transaction.</summary>
public sealed class TimelineWriter
{
    /// <summary>Stores summaries for locally committed log records in the same transaction.</summary>
    public async Task AppendLocalAsync(IEnumerable<(EventTimelineEntry Entry, long LogRowId)> entries, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = Options(connection);
        await using var db = new OperationsDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        foreach (var batch in entries.Chunk(500))
        {
            foreach (var (entry, logRowId) in batch)
                db.TimelineRecords.Add(new TimelineRecord { MessageId = Guid.CreateVersion7(), LocalLogRowId = logRowId, Source = entry.Source,
                    Time = entry.Time.ToUniversalTime(), ResourceId = entry.ResourceId, ResourceKind = entry.ResourceKind,
                    Summary = entry.Summary, Evidence = entry.Evidence, Link = entry.Link, RuleLink = entry.RuleLink });
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
        }
    }

    public async Task AppendAsync(EventTimelineDelivery delivery, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var entry = delivery.Entry;
        if (delivery.MessageId.Version != 7 || entry is null || entry.ResourceId.Version != 7 || entry.Time == default ||
            !TimelineReader.ValidSourceKind(entry.Source, entry.ResourceKind) ||
            entry.Summary is null || entry.Evidence is null || entry.Link is null)
            throw new ArgumentException("Invalid timeline event.", nameof(delivery));
        await using var db = new OperationsDbContext(Options(connection));
        await db.Database.UseTransactionAsync(transaction, token);
        var existing = await db.TimelineRecords.FromSqlInterpolated($"SELECT * FROM [operations].[TimelineRecords] WITH (UPDLOCK, HOLDLOCK) WHERE [MessageId] = {delivery.MessageId}")
            .SingleOrDefaultAsync(token);
        if (existing is not null)
        {
            if (existing.Source != entry.Source || existing.Time != entry.Time || existing.ResourceId != entry.ResourceId ||
                existing.ResourceKind != entry.ResourceKind || existing.Summary != entry.Summary || existing.Evidence != entry.Evidence ||
                existing.Link != entry.Link || existing.RuleLink != entry.RuleLink)
                throw new InvalidOperationException("A different timeline event already uses this message ID.");
            return;
        }
        db.TimelineRecords.Add(new TimelineRecord { MessageId = delivery.MessageId, Source = entry.Source, Time = entry.Time.ToUniversalTime(),
            ResourceId = entry.ResourceId, ResourceKind = entry.ResourceKind, Summary = entry.Summary, Evidence = entry.Evidence,
            Link = entry.Link, RuleLink = entry.RuleLink });
        await db.SaveChangesAsync(token);
    }

    private static DbContextOptions<OperationsDbContext> Options(DbConnection connection) => new DbContextOptionsBuilder<OperationsDbContext>()
        .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
            .MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
}
