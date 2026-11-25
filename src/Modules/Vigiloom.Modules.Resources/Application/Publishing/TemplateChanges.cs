using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Modules.Resources.Application.Publishing;

/// <summary>Previews and applies changes to templates and their assigned devices.</summary>
public sealed class TemplateChanges(IDbContextFactory<ResourcesDbContext> contexts, PublicTemplates templates,
    IDataProtectionProvider protection, TimeProvider clock, ResourceWork outbox)
{
    private readonly IDataProtector updateProtector = protection.CreateProtector("Vigiloom.ResourceTemplate.Update.v1");
    private readonly IDataProtector retireProtector = protection.CreateProtector("Vigiloom.ResourceTemplate.Retire.v1");
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public async Task<TemplateOperationResult<PublicationImpact>> ImpactAsync(Guid id, int page, CancellationToken token)
    {
        if (id.Version != 7 || page is < 1 or > int.MaxValue / 50) return new(TemplateOperationStatus.Invalid);
        await using var db = await contexts.CreateDbContextAsync(token);
        var version = await db.PublicTemplates.AsNoTracking().Where(t => t.Id == id && !t.IsRetired).Select(t => t.Version).SingleOrDefaultAsync(token);
        if (version is null) return new(TemplateOperationStatus.NotFound);
        var impact = await TemplateImpact.ReadAsync(db, id, version, page, token);
        var latest = await db.PublicTemplates.AsNoTracking().Where(t => t.Id == id && !t.IsRetired).Select(t => t.Version).SingleOrDefaultAsync(token);
        return latest is not null && version.AsSpan().SequenceEqual(latest) ? new(TemplateOperationStatus.Success, impact) : new(TemplateOperationStatus.Conflict);
    }

    public async Task<TemplateOperationResult<PublicationTemplateUpdatePreview>> PreviewUpdateAsync(Guid id, PublicationTemplateUpdate input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || id.Version != 7) return new(TemplateOperationStatus.Invalid);
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.PublicTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && !t.IsRetired, token);
        if (row is null) return new(TemplateOperationStatus.NotFound);
        PublicTemplate candidate;
        try { candidate = new(input.Name!, input.ShowLatency, input.ShowObservedAt, clock.GetUtcNow(), input.HistoryDays ?? row.HistoryDays); }
        catch (ArgumentException) { return new(TemplateOperationStatus.Invalid); }
        var impact = await ImpactAsync(id, 1, token);
        if (impact.Status != TemplateOperationStatus.Success || impact.Value!.TemplateVersion != Convert.ToBase64String(row.Version)) return new(TemplateOperationStatus.Conflict);
        var proposed = new PublicationTemplateUpdate(candidate.Name, input.ShowLatency, input.ShowObservedAt, candidate.HistoryDays);
        var proof = new UpdateProof(actor, id, impact.Value.TemplateVersion, impact.Value.Fingerprint, proposed, clock.GetUtcNow().ToUniversalTime());
        return new(TemplateOperationStatus.Success, new(Map(row), proposed, impact.Value, updateProtector.Protect(JsonSerializer.Serialize(proof))));
    }

    public async Task<TemplateOperationResult<PublicationTemplateItem>> ConfirmUpdateAsync(Guid id, ConfirmPublicationTemplateUpdate input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || id.Version != 7 || input.Confirmation is not { Length: > 0 and <= 8192 }) return new(TemplateOperationStatus.Invalid);
        UpdateProof? proof;
        try { proof = JsonSerializer.Deserialize<UpdateProof>(updateProtector.Unprotect(input.Confirmation)); }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { return new(TemplateOperationStatus.Invalid); }
        if (proof is null || proof.Actor != actor || proof.TemplateId != id || proof.Proposed is null || !ValidTime(proof.IssuedAt, clock.GetUtcNow())) return new(TemplateOperationStatus.Invalid);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await PublicationMutationLock.TryAcquireAsync(db, token)) return new(TemplateOperationStatus.Conflict);
        var row = await db.PublicTemplates.SingleOrDefaultAsync(t => t.Id == id && !t.IsRetired, token);
        if (row is null) return new(TemplateOperationStatus.NotFound);
        if (Convert.ToBase64String(row.Version) != proof.Version) return new(TemplateOperationStatus.Conflict);
        var impact = await TemplateImpact.ReadAsync(db, id, row.Version, 1, token);
        if (impact.Fingerprint != proof.Fingerprint) return new(TemplateOperationStatus.Conflict);
        var now = clock.GetUtcNow().ToUniversalTime();
        if (!ValidTime(proof.IssuedAt, now)) return new(TemplateOperationStatus.Invalid);
        try
        {
            row.Update(proof.Proposed.Name!, proof.Proposed.ShowLatency, proof.Proposed.ShowObservedAt, now, proof.Proposed.HistoryDays ?? row.HistoryDays);
            await db.SaveChangesAsync(token);

            await outbox.PublishAsync(db, new PublicTemplateAuditRecorded(actor, id, now, PublicationTemplateAction.Updated, impact.Count), token);
            await db.Database.CurrentTransaction!.CommitAsync(token);
        }
        catch (DbUpdateConcurrencyException) { return new(TemplateOperationStatus.Conflict); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { return new(TemplateOperationStatus.Conflict); }
        if (row.HistorySince == PublicTemplate.PendingHistorySince) await templates.ConfirmHistoryAsync(id, row.Version, token);
        return new(TemplateOperationStatus.Success, await templates.GetAsync(id, false, token));
    }

    public async Task<TemplateOperationResult<PublicationTemplateRetirementPreview>> PreviewRetireAsync(Guid id, PublicationTemplateRetirement input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || id.Version != 7) return new(TemplateOperationStatus.Invalid);
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.PublicTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && !t.IsRetired, token);
        if (row is null) return new(TemplateOperationStatus.NotFound);
        var (replacement, valid) = await ReplacementAsync(db, row, input, token);
        if (!valid) return new(TemplateOperationStatus.Invalid);
        var impact = await ImpactAsync(id, 1, token);
        if (impact.Status != TemplateOperationStatus.Success || impact.Value!.TemplateVersion != Convert.ToBase64String(row.Version)) return new(TemplateOperationStatus.Conflict);
        var proof = new RetireProof(actor, id, impact.Value.TemplateVersion, impact.Value.Fingerprint, input, clock.GetUtcNow().ToUniversalTime());
        return new(TemplateOperationStatus.Success, new(Map(row), replacement is null ? null : Map(replacement), impact.Value,
            retireProtector.Protect(JsonSerializer.Serialize(proof))));
    }

    public async Task<TemplateOperationStatus> ConfirmRetireAsync(Guid id, ConfirmPublicationTemplateRetirement input, Guid actor, CancellationToken token)
    {
        if (actor == Guid.Empty || id.Version != 7 || input.Confirmation is not { Length: > 0 and <= 8192 }) return TemplateOperationStatus.Invalid;
        RetireProof? proof;
        try { proof = JsonSerializer.Deserialize<RetireProof>(retireProtector.Unprotect(input.Confirmation)); }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { return TemplateOperationStatus.Invalid; }
        if (proof is null || proof.Actor != actor || proof.TemplateId != id || proof.Choice is null || !ValidTime(proof.IssuedAt, clock.GetUtcNow())) return TemplateOperationStatus.Invalid;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await PublicationMutationLock.TryAcquireAsync(db, token)) return TemplateOperationStatus.Conflict;
        var row = await db.PublicTemplates.SingleOrDefaultAsync(t => t.Id == id && !t.IsRetired, token);
        if (row is null) return TemplateOperationStatus.NotFound;
        if (Convert.ToBase64String(row.Version) != proof.Version) return TemplateOperationStatus.Conflict;
        var (replacement, valid) = await ReplacementAsync(db, row, proof.Choice, token);
        if (!valid) return TemplateOperationStatus.Conflict;
        var impact = await TemplateImpact.ReadAsync(db, id, row.Version, 1, token);
        if (impact.Fingerprint != proof.Fingerprint) return TemplateOperationStatus.Conflict;
        var now = clock.GetUtcNow().ToUniversalTime();
        if (!ValidTime(proof.IssuedAt, now)) return TemplateOperationStatus.Invalid;
        var wasDefault = row.IsDefault;
        try
        {
            row.Retire(now);
            await db.SaveChangesAsync(token);
            if (replacement is not null) { replacement.SetDefault(true, now); await db.SaveChangesAsync(token); }

            await outbox.PublishAsync(db, new PublicTemplateAuditRecorded(actor, id, now, PublicationTemplateAction.Retired, impact.Count), token);
            if (wasDefault) await outbox.PublishAsync(db, new PublicTemplateAuditRecorded(actor, replacement?.Id ?? id, now,
                replacement is null ? PublicationTemplateAction.DefaultCleared : PublicationTemplateAction.DefaultChanged, 0), token);
            await db.Database.CurrentTransaction!.CommitAsync(token);
        }
        catch (DbUpdateConcurrencyException) { return TemplateOperationStatus.Conflict; }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { return TemplateOperationStatus.Conflict; }
        return TemplateOperationStatus.Success;
    }

    private static async Task<(PublicTemplate? Replacement, bool Valid)> ReplacementAsync(ResourcesDbContext db, PublicTemplate current,
        PublicationTemplateRetirement input, CancellationToken token)
    {
        if (!current.IsDefault) return (null, input.ReplacementDefaultId is null && input.ReplacementDefaultVersion is null);
        if (input.ReplacementDefaultId is not { } id)
            return (null, input.ReplacementDefaultVersion is null && !await db.PublicTemplates.AnyAsync(t => !t.IsRetired && t.Id != current.Id, token));
        if (id.Version != 7 || id == current.Id || string.IsNullOrEmpty(input.ReplacementDefaultVersion)) return (null, false);
        var replacement = await db.PublicTemplates.SingleOrDefaultAsync(t => t.Id == id && !t.IsRetired, token);
        return (replacement, replacement is not null && Convert.ToBase64String(replacement.Version) == input.ReplacementDefaultVersion);
    }

    private static PublicationTemplateItem Map(PublicTemplate row) => new(row.Id, row.Name, row.ShowLatency, row.ShowObservedAt,
        row.IsDefault, Convert.ToBase64String(row.Version), HistoryDays: row.HistoryDays,
        HistoryReady: row.HistoryDays > 0 && row.HistorySince != PublicTemplate.PendingHistorySince);
    private static bool ValidTime(DateTimeOffset issued, DateTimeOffset now) => issued <= now && now - issued <= Lifetime;
    private sealed record UpdateProof(Guid Actor, Guid TemplateId, string Version, string Fingerprint, PublicationTemplateUpdate Proposed, DateTimeOffset IssuedAt);
    private sealed record RetireProof(Guid Actor, Guid TemplateId, string Version, string Fingerprint, PublicationTemplateRetirement Choice, DateTimeOffset IssuedAt);
}
