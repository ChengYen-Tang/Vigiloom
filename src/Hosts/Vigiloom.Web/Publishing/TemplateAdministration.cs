using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Web.Publishing;

internal enum TemplateChangeStatus { Created, Updated, NotFound, Invalid, Conflict }
internal sealed record TemplateChangeResult(TemplateChangeStatus Status, PublicationTemplateItem? Template = null);

internal sealed class TemplateAdministration(IDbContextFactory<ResourcesDbContext> resources,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    internal async Task<TemplateChangeResult> CreateAsync(CreatePublicationTemplate input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty) throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        var now = clock.GetUtcNow().ToUniversalTime();
        PublicationTemplate template;
        try { template = new(input.Name!, input.ShowLatency, input.ShowObservedAt, now, input.HistoryDays); }
        catch (ArgumentException) { return new(TemplateChangeStatus.Invalid); }
        await using var resourcesDb = await resources.CreateDbContextAsync(token);
        await using var transaction = await resourcesDb.Database.BeginTransactionAsync(token);
        if (!await PublicationMutationLock.TryAcquireAsync(resourcesDb, token)) return new(TemplateChangeStatus.Conflict);
        var options = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(resourcesDb.Database.GetDbConnection()).Options;
        await using var templates = new PublishingDbContext(options);
        await templates.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
        templates.Templates.Add(template);
        try { await templates.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        { return new(TemplateChangeStatus.Conflict); }
        await audit.AppendAsync(resourcesDb.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, template.Id, now, new PublicationTemplateChanged(PublicationTemplateAction.Created, 0)), token);
        await transaction.CommitAsync(token);
        var confirmed = template.HistorySince == PublicationTemplate.PendingHistorySince
            ? await TemplateHistoryPeriod.ConfirmAsync(resources, template.Id, template.Version, token) : null;
        return new(TemplateChangeStatus.Created, new(template.Id, template.Name, template.ShowLatency, template.ShowObservedAt,
            template.IsDefault, Convert.ToBase64String(confirmed ?? template.Version), HistoryDays: template.HistoryDays,
            HistoryReady: template.HistoryDays > 0 && (confirmed is not null || template.HistorySince != PublicationTemplate.PendingHistorySince)));
    }

    internal async Task<TemplateChangeResult> SetDefaultAsync(Guid id, SetDefaultPublicationTemplate input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty) throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        if (id.Version != 7 || !TryVersion(input.Version, out var version)) return new(TemplateChangeStatus.Invalid);
        byte[] expectedDefaultVersion = [];
        if (input.ExpectedDefaultId is { } expectedId ? expectedId.Version != 7 || !TryVersion(input.ExpectedDefaultVersion, out expectedDefaultVersion) : input.ExpectedDefaultVersion is not null)
            return new(TemplateChangeStatus.Invalid);
        await using var resourcesDb = await resources.CreateDbContextAsync(token);
        await using var transaction = await resourcesDb.Database.BeginTransactionAsync(token);
        if (!await PublicationMutationLock.TryAcquireAsync(resourcesDb, token)) return new(TemplateChangeStatus.Conflict);
        var options = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(resourcesDb.Database.GetDbConnection()).Options;
        await using var templates = new PublishingDbContext(options);
        await templates.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
        var template = await templates.Templates.SingleOrDefaultAsync(x => x.Id == id && !x.IsRetired, token);
        if (template is null) return new(TemplateChangeStatus.NotFound);
        if (!template.Version.AsSpan().SequenceEqual(version)) return new(TemplateChangeStatus.Conflict);
        var previous = await templates.Templates.SingleOrDefaultAsync(x => x.IsDefault, token);
        if (previous?.Id != input.ExpectedDefaultId || (previous is not null && !previous.Version.AsSpan().SequenceEqual(expectedDefaultVersion)))
            return new(TemplateChangeStatus.Conflict);
        if (!template.IsDefault)
        {
            var now = clock.GetUtcNow().ToUniversalTime();
            try
            {
                if (previous is not null)
                {
                    previous.SetDefault(false, now);
                    await templates.SaveChangesAsync(token);
                }
                template.SetDefault(true, now);
                await templates.SaveChangesAsync(token);
            }
            catch (DbUpdateConcurrencyException) { return new(TemplateChangeStatus.Conflict); }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            { return new(TemplateChangeStatus.Conflict); }
            await audit.AppendAsync(resourcesDb.Database.GetDbConnection(), transaction.GetDbTransaction(),
                new OperationAudit(actor, template.Id, now, new PublicationTemplateChanged(PublicationTemplateAction.DefaultChanged, 0)), token);
            await transaction.CommitAsync(token);
        }
        return new(TemplateChangeStatus.Updated, new(template.Id, template.Name, template.ShowLatency, template.ShowObservedAt,
            template.IsDefault, Convert.ToBase64String(template.Version), HistoryDays: template.HistoryDays,
            HistoryReady: template.HistoryDays > 0 && template.HistorySince != PublicationTemplate.PendingHistorySince));
    }

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        try { version = Convert.FromBase64String(value ?? ""); return version.Length == 8; }
        catch (FormatException) { return false; }
    }
}
