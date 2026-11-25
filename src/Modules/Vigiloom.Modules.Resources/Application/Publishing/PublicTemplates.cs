using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vigiloom.Modules.Resources.Application.Publishing;

/// <summary>Owns publication templates and their default selection.</summary>
public sealed class PublicTemplates(IDbContextFactory<ResourcesDbContext> contexts, TimeProvider clock, IDbContextOutbox outbox)
{
    public async Task<PublicationTemplatePage> ListAsync(int page, CancellationToken token)
    {
        const int size = 50;
        if (page is < 1 or > int.MaxValue / size) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.PublicTemplates.AsNoTracking().Where(t => !t.IsRetired).OrderBy(t => t.Name).ThenBy(t => t.RowId)
            .Skip((page - 1) * size).Take(size + 1).ToArrayAsync(token);
        var ids = rows.Take(size).Select(t => t.Id).ToArray();
        var counts = await db.Devices.AsNoTracking().Where(d => !d.IsArchived && d.PublicTemplateId != null && ids.Contains(d.PublicTemplateId.Value))
            .GroupBy(d => d.PublicTemplateId!.Value).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count, token);
        return new(rows.Take(size).Select(t => Map(t, counts.GetValueOrDefault(t.Id))).ToArray(), page, rows.Length > size);
    }

    public async Task<PublicationTemplateItem?> GetAsync(Guid? id, bool defaultOnly, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.PublicTemplates.AsNoTracking().SingleOrDefaultAsync(t => !t.IsRetired && (defaultOnly ? t.IsDefault : t.Id == id), token);
        return row is null ? null : Map(row);
    }

    public async Task<TemplateOperationResult<PublicationTemplateItem>> CreateAsync(CreatePublicationTemplate input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty) return new(TemplateOperationStatus.Invalid);
        var now = clock.GetUtcNow().ToUniversalTime();
        PublicTemplate row;
        try { row = new(input.Name!, input.ShowLatency, input.ShowObservedAt, now, input.HistoryDays); }
        catch (ArgumentException) { return new(TemplateOperationStatus.Invalid); }
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await PublicationMutationLock.TryAcquireAsync(db, token)) return new(TemplateOperationStatus.Conflict);
        db.PublicTemplates.Add(row);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { return new(TemplateOperationStatus.Conflict); }
        outbox.Enroll(db);
        await outbox.PublishAsync(new PublicTemplateAuditRecorded(actor, row.Id, now, PublicationTemplateAction.Created, 0));
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        if (row.HistorySince == PublicTemplate.PendingHistorySince)
            await ConfirmHistoryAsync(row.Id, row.Version, token);
        return new(TemplateOperationStatus.Success, await GetAsync(row.Id, false, token));
    }

    public async Task<TemplateOperationResult<PublicationTemplateItem>> SetDefaultAsync(Guid id, SetDefaultPublicationTemplate input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || id.Version != 7 || !TryVersion(input.Version, out var version)) return new(TemplateOperationStatus.Invalid);
        byte[] expectedDefault = [];
        if (input.ExpectedDefaultId is { } expectedId ? expectedId.Version != 7 || !TryVersion(input.ExpectedDefaultVersion, out expectedDefault) : input.ExpectedDefaultVersion is not null)
            return new(TemplateOperationStatus.Invalid);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await PublicationMutationLock.TryAcquireAsync(db, token)) return new(TemplateOperationStatus.Conflict);
        var row = await db.PublicTemplates.SingleOrDefaultAsync(t => t.Id == id && !t.IsRetired, token);
        if (row is null) return new(TemplateOperationStatus.NotFound);
        if (!row.Version.AsSpan().SequenceEqual(version)) return new(TemplateOperationStatus.Conflict);
        var previous = await db.PublicTemplates.SingleOrDefaultAsync(t => t.IsDefault, token);
        if (previous?.Id != input.ExpectedDefaultId || previous is not null && !previous.Version.AsSpan().SequenceEqual(expectedDefault))
            return new(TemplateOperationStatus.Conflict);
        if (row.IsDefault) return new(TemplateOperationStatus.Success, Map(row));
        var now = clock.GetUtcNow().ToUniversalTime();
        try
        {
            if (previous is not null) { previous.SetDefault(false, now); await db.SaveChangesAsync(token); }
            row.SetDefault(true, now);
            await db.SaveChangesAsync(token);
            outbox.Enroll(db);
            await outbox.PublishAsync(new PublicTemplateAuditRecorded(actor, row.Id, now, PublicationTemplateAction.DefaultChanged, 0));
            await outbox.SaveChangesAndFlushMessagesAsync(token);
        }
        catch (DbUpdateConcurrencyException) { return new(TemplateOperationStatus.Conflict); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { return new(TemplateOperationStatus.Conflict); }
        return new(TemplateOperationStatus.Success, Map(row));
    }

    private async Task ConfirmHistoryAsync(Guid id, byte[] version, CancellationToken token)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (!await PublicationMutationLock.TryAcquireAsync(db, token)) return;
            var row = await db.PublicTemplates.SingleOrDefaultAsync(t => t.Id == id && !t.IsRetired, token);
            if (row is null || row.HistorySince != PublicTemplate.PendingHistorySince || !row.Version.AsSpan().SequenceEqual(version)) return;
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            row.ConfirmHistorySince(now);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateConcurrencyException) { }
        catch (Exception error) when (error is DbException or DbUpdateException && !token.IsCancellationRequested) { }
    }

    private static PublicationTemplateItem Map(PublicTemplate row, int count = 0) =>
        new(row.Id, row.Name, row.ShowLatency, row.ShowObservedAt, row.IsDefault, Convert.ToBase64String(row.Version), count,
            row.HistoryDays, row.HistoryDays > 0 && row.HistorySince != PublicTemplate.PendingHistorySince);

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        try { version = Convert.FromBase64String(value ?? ""); return version.Length == 8; }
        catch (FormatException) { return false; }
    }
}
