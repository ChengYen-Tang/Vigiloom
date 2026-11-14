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

internal sealed class TemplateRetirementWorkflow(IDbContextFactory<PublishingDbContext> publishing, IDbContextFactory<ResourcesDbContext> resources,
    TemplateReader reader, IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.PublicationTemplate.RetirementPreview.v1");
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal async Task<(PublicationTemplateRetirementPreview? Preview, TemplateChangeStatus Status)> PreviewAsync(Guid id,
        PublicationTemplateRetirement input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || id.Version != 7) return (null, TemplateChangeStatus.Invalid);
        await using var db = await publishing.CreateDbContextAsync(token);
        var row = await db.Templates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.IsRetired, token);
        if (row is null) return (null, TemplateChangeStatus.NotFound);
        var (replacement, valid) = await ReplacementAsync(db, row, input, token);
        if (!valid) return (null, TemplateChangeStatus.Invalid);
        var impact = await reader.ImpactAsync(id, 1, token);
        if (impact.Changed || impact.TooMany || impact.Impact is null || impact.Impact.TemplateVersion != Convert.ToBase64String(row.Version))
            return (null, TemplateChangeStatus.Conflict);
        var proof = new Proof(actor, id, impact.Impact.TemplateVersion, impact.Impact.Fingerprint, input, clock.GetUtcNow().ToUniversalTime());
        return (new(Map(row), replacement is null ? null : Map(replacement), impact.Impact, protector.Protect(JsonSerializer.Serialize(proof))), TemplateChangeStatus.Updated);
    }

    internal async Task<TemplateChangeStatus> ConfirmAsync(Guid id, ConfirmPublicationTemplateRetirement input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || id.Version != 7 || input.Confirmation is not { Length: > 0 and <= 8192 }) return TemplateChangeStatus.Invalid;
        Proof? proof;
        try { proof = JsonSerializer.Deserialize<Proof>(protector.Unprotect(input.Confirmation)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return TemplateChangeStatus.Invalid; }
        var now = clock.GetUtcNow().ToUniversalTime();
        if (proof is null || proof.Actor != actor || proof.TemplateId != id || proof.Choice is null || !ValidTime(proof, now)) return TemplateChangeStatus.Invalid;
        await using var resourcesDb = await resources.CreateDbContextAsync(token);
        await using var transaction = await resourcesDb.Database.BeginTransactionAsync(token);
        if (!await PublicationMutationLock.TryAcquireAsync(resourcesDb, token)) return TemplateChangeStatus.Conflict;
        var options = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(resourcesDb.Database.GetDbConnection()).Options;
        await using var templates = new PublishingDbContext(options);
        await templates.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
        var row = await templates.Templates.SingleOrDefaultAsync(x => x.Id == id && !x.IsRetired, token);
        if (row is null) return TemplateChangeStatus.NotFound;
        if (Convert.ToBase64String(row.Version) != proof.Version) return TemplateChangeStatus.Conflict;
        var (replacement, valid) = await ReplacementAsync(templates, row, proof.Choice, token);
        if (!valid) return TemplateChangeStatus.Conflict;
        var impact = await TemplateImpact.ReadAsync(resourcesDb, id, row.Version, 1, token);
        if (impact is null || impact.Fingerprint != proof.Fingerprint) return TemplateChangeStatus.Conflict;
        now = clock.GetUtcNow().ToUniversalTime();
        if (!ValidTime(proof, now)) return TemplateChangeStatus.Invalid;
        var wasDefault = row.IsDefault;
        try
        {
            row.Retire(now);
            await templates.SaveChangesAsync(token);
            if (replacement is not null)
            {
                replacement.SetDefault(true, now);
                await templates.SaveChangesAsync(token);
            }
        }
        catch (DbUpdateConcurrencyException) { return TemplateChangeStatus.Conflict; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return TemplateChangeStatus.Conflict; }
        await audit.AppendAsync(resourcesDb.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, id, now, new PublicationTemplateChanged(PublicationTemplateAction.Retired, impact.Count)), token);
        if (wasDefault)
            await audit.AppendAsync(resourcesDb.Database.GetDbConnection(), transaction.GetDbTransaction(),
                new OperationAudit(actor, replacement?.Id ?? id, now, new PublicationTemplateChanged(
                    replacement is null ? PublicationTemplateAction.DefaultCleared : PublicationTemplateAction.DefaultChanged, 0)), token);
        await transaction.CommitAsync(token);
        return TemplateChangeStatus.Updated;
    }

    private static async Task<(PublicationTemplate? Replacement, bool Valid)> ReplacementAsync(PublishingDbContext db, PublicationTemplate current,
        PublicationTemplateRetirement input, CancellationToken token)
    {
        if (!current.IsDefault) return (null, input.ReplacementDefaultId is null && input.ReplacementDefaultVersion is null);
        if (input.ReplacementDefaultId is not { } id)
            return (null, input.ReplacementDefaultVersion is null && !await db.Templates.AnyAsync(x => !x.IsRetired && x.Id != current.Id, token));
        if (id.Version != 7 || id == current.Id || string.IsNullOrEmpty(input.ReplacementDefaultVersion)) return (null, false);
        var replacement = await db.Templates.SingleOrDefaultAsync(x => x.Id == id && !x.IsRetired, token);
        return (replacement, replacement is not null && Convert.ToBase64String(replacement.Version) == input.ReplacementDefaultVersion);
    }

    private static PublicationTemplateItem Map(PublicationTemplate row) => new(row.Id, row.Name, row.ShowLatency, row.ShowObservedAt, row.IsDefault, Convert.ToBase64String(row.Version));
    private static bool ValidTime(Proof proof, DateTimeOffset now) => proof.IssuedAt <= now && now - proof.IssuedAt <= Lifetime;
    private sealed record Proof(Guid Actor, Guid TemplateId, string Version, string Fingerprint, PublicationTemplateRetirement Choice, DateTimeOffset IssuedAt);
}
