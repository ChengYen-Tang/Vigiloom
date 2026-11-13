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

internal enum TemplateChangeStatus { Created, Invalid, Conflict }
internal sealed record TemplateChangeResult(TemplateChangeStatus Status, PublicationTemplateItem? Template = null);

internal sealed class TemplateAdministration(IDbContextFactory<ResourcesDbContext> resources,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    internal async Task<TemplateChangeResult> CreateAsync(CreatePublicationTemplate input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty) throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        var now = clock.GetUtcNow().ToUniversalTime();
        PublicationTemplate template;
        try { template = new(input.Name!, input.ShowLatency, input.ShowObservedAt, now); }
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
        return new(TemplateChangeStatus.Created, new(template.Id, template.Name, template.ShowLatency, template.ShowObservedAt,
            template.IsDefault, Convert.ToBase64String(template.Version)));
    }
}
