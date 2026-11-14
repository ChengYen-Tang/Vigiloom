using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
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

internal sealed class TemplateUpdateWorkflow(IDbContextFactory<PublishingDbContext> publishing, IDbContextFactory<ResourcesDbContext> resources,
    TemplateReader reader, IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.PublicationTemplate.UpdatePreview.v1");
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal async Task<(PublicationTemplateUpdatePreview? Preview, TemplateChangeStatus Status)> PreviewAsync(Guid id,
        PublicationTemplateUpdate input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || id.Version != 7) return (null, TemplateChangeStatus.Invalid);
        await using var db = await publishing.CreateDbContextAsync(token);
        var row = await db.Templates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.IsRetired, token);
        if (row is null) return (null, TemplateChangeStatus.NotFound);
        PublicationTemplate candidate;
        try { candidate = new(input.Name!, input.ShowLatency, input.ShowObservedAt, clock.GetUtcNow(), input.HistoryDays ?? row.HistoryDays); }
        catch (ArgumentException) { return (null, TemplateChangeStatus.Invalid); }
        var impact = await reader.ImpactAsync(id, 1, token);
        if (impact.Changed || impact.TooMany) return (null, TemplateChangeStatus.Conflict);
        if (impact.Impact is null || impact.Impact.TemplateVersion != Convert.ToBase64String(row.Version)) return (null, TemplateChangeStatus.Conflict);
        var proposed = new PublicationTemplateUpdate(candidate.Name, input.ShowLatency, input.ShowObservedAt, candidate.HistoryDays);
        var proof = new Proof(actor, id, impact.Impact.TemplateVersion, impact.Impact.Fingerprint, proposed, clock.GetUtcNow().ToUniversalTime());
        var current = new PublicationTemplateItem(row.Id, row.Name, row.ShowLatency, row.ShowObservedAt, row.IsDefault, Convert.ToBase64String(row.Version),
            HistoryDays: row.HistoryDays, HistoryReady: row.HistoryDays > 0 && row.HistorySince != PublicationTemplate.PendingHistorySince);
        return (new(current, proposed, impact.Impact, protector.Protect(JsonSerializer.Serialize(proof))), TemplateChangeStatus.Updated);
    }

    internal async Task<TemplateChangeResult> ConfirmAsync(Guid id, ConfirmPublicationTemplateUpdate input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || id.Version != 7 || input.Confirmation is not { Length: > 0 and <= 8192 }) return new(TemplateChangeStatus.Invalid);
        Proof? proof;
        try { proof = JsonSerializer.Deserialize<Proof>(protector.Unprotect(input.Confirmation)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return new(TemplateChangeStatus.Invalid); }
        var now = clock.GetUtcNow().ToUniversalTime();
        if (proof is null || proof.Actor != actor || proof.TemplateId != id || proof.Proposed is null || proof.IssuedAt > now || now - proof.IssuedAt > Lifetime)
            return new(TemplateChangeStatus.Invalid);
        await using var resourcesDb = await resources.CreateDbContextAsync(token);
        await using var transaction = await resourcesDb.Database.BeginTransactionAsync(token);
        if (!await PublicationMutationLock.TryAcquireAsync(resourcesDb, token)) return new(TemplateChangeStatus.Conflict);
        var options = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(resourcesDb.Database.GetDbConnection()).Options;
        await using var templates = new PublishingDbContext(options);
        await templates.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
        var row = await templates.Templates.SingleOrDefaultAsync(x => x.Id == id && !x.IsRetired, token);
        if (row is null) return new(TemplateChangeStatus.NotFound);
        if (Convert.ToBase64String(row.Version) != proof.Version) return new(TemplateChangeStatus.Conflict);
        var impact = await TemplateImpact.ReadAsync(resourcesDb, id, row.Version, 1, token);
        if (impact is null || impact.Fingerprint != proof.Fingerprint) return new(TemplateChangeStatus.Conflict);
        now = clock.GetUtcNow().ToUniversalTime();
        if (proof.IssuedAt > now || now - proof.IssuedAt > Lifetime) return new(TemplateChangeStatus.Invalid);
        try
        {
            row.Update(proof.Proposed.Name!, proof.Proposed.ShowLatency, proof.Proposed.ShowObservedAt, now, proof.Proposed.HistoryDays ?? row.HistoryDays);
            await templates.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException) { return new(TemplateChangeStatus.Conflict); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(TemplateChangeStatus.Conflict); }
        await audit.AppendAsync(resourcesDb.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, id, now, new PublicationTemplateChanged(PublicationTemplateAction.Updated, impact.Count)), token);
        await transaction.CommitAsync(token);
        var confirmed = row.HistorySince == PublicationTemplate.PendingHistorySince
            ? await TemplateHistoryPeriod.ConfirmAsync(resources, row.Id, row.Version, token) : null;
        return new(TemplateChangeStatus.Updated, new(row.Id, row.Name, row.ShowLatency, row.ShowObservedAt, row.IsDefault,
            Convert.ToBase64String(confirmed ?? row.Version), HistoryDays: row.HistoryDays,
            HistoryReady: row.HistoryDays > 0 && (confirmed is not null || row.HistorySince != PublicationTemplate.PendingHistorySince)));
    }

    private sealed record Proof(Guid Actor, Guid TemplateId, string Version, string Fingerprint, PublicationTemplateUpdate Proposed, DateTimeOffset IssuedAt);
}
