using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

public enum CustomBaselineStageStatus { Staged, Unchanged, Invalid, NotFound, Conflict, LimitReached, Revoked }
public sealed record CustomBaselineStageResult(CustomBaselineStageStatus Status, Guid? SourceId = null, Guid? RevisionId = null);
public sealed record CustomBaselineDraft(BaselineCheck Check, string SourceVersion);

/// <summary>Stages administrator-authored checks; publication remains a separate audited action.</summary>
public sealed class CustomBaselineStage(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    public async Task<CustomBaselineStageResult> SaveAsync(Guid? sourceId, string? expectedVersion, BaselineCheck input, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty || input is null) return new(CustomBaselineStageStatus.Invalid);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        IntelligenceSource source;
        if (sourceId is { } id)
        {
            var existing = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
            if (existing is null || !BaselineEligibility.IsCustomKey(existing.Key, existing.Id)) return new(CustomBaselineStageStatus.NotFound);
            if (expectedVersion != Convert.ToBase64String(existing.Version)) return new(CustomBaselineStageStatus.Conflict);
            source = existing;
        }
        else
        {
            if (await db.IntelligenceSources.CountAsync(x => x.Key.StartsWith(BaselineSources.LocalPrefix), token) >= 98)
                return new(CustomBaselineStageStatus.LimitReached);
            source = new IntelligenceSource { Key = "pending", Enabled = true };
            source.Key = $"{BaselineSources.LocalPrefix}{source.Id:D}";
        }
        var check = input with { Id = $"{CustomBaselineCheck.Prefix}{source.Id:D}" };
        if (!CustomBaselineCheck.IsValid(check)) return new(CustomBaselineStageStatus.Invalid);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(check);
        var digest = SHA256.HashData(bytes);
        var existingRevision = sourceId is null ? null : await db.IntelligenceRevisions.SingleOrDefaultAsync(
            x => x.SourceId == source.Id && x.Digest == digest && x.MappingVersion == BaselineEligibility.CustomMapping, token);
        if (existingRevision is { Revoked: true }) return new(CustomBaselineStageStatus.Revoked);
        if (existingRevision is not null) return new(CustomBaselineStageStatus.Unchanged, source.Id, existingRevision.Id);
        var revision = new IntelligenceRevision
        {
            SourceId = source.Id, UpstreamVersion = Guid.CreateVersion7().ToString("D"), RetrievedAt = clock.GetUtcNow().ToUniversalTime(),
            Artifact = bytes, Digest = digest, SchemaVersion = BaselineDefinitions.Schema, MappingVersion = BaselineEligibility.CustomMapping,
            LicenseEvidence = $"Locally configured; {check.License}", RecordCount = 1, Normalized = true
        };
        if (sourceId is null) db.IntelligenceSources.Add(source);
        db.IntelligenceRevisions.Add(revision);
        source.LastObservedRevisionId = revision.Id;
        source.LastSuccessfulCheckAt = revision.RetrievedAt;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(CustomBaselineStageStatus.Staged, source.Id, revision.Id);
    }

    public async Task<BaselineCheck?> ReadAsync(Guid sourceId, Guid revisionId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, token);
        if (source is null || !BaselineEligibility.IsCustomKey(source.Key, source.Id)) return null;
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId && x.SourceId == sourceId, token);
        return revision is null ? null : BaselineEligibility.ReadCustom(revision, sourceId);
    }

    public async Task<CustomBaselineDraft?> ReadDraftAsync(Guid sourceId, Guid revisionId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, token);
        if (source is null || !BaselineEligibility.IsCustomKey(source.Key, source.Id)) return null;
        var revision = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId && x.SourceId == sourceId, token);
        var check = revision is null ? null : BaselineEligibility.ReadCustom(revision, sourceId);
        return check is null ? null : new(check, Convert.ToBase64String(source.Version));
    }

    public async Task<IReadOnlyDictionary<Guid, BaselineCheck>> ReadVersionsAsync(Guid sourceId, IReadOnlyList<Guid> revisionIds, CancellationToken token)
    {
        if (revisionIds.Count == 0) return new Dictionary<Guid, BaselineCheck>();
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, token);
        if (source is null || !BaselineEligibility.IsCustomKey(source.Key, source.Id)) return new Dictionary<Guid, BaselineCheck>();
        var revisions = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == sourceId && revisionIds.Contains(x.Id)).ToArrayAsync(token);
        return revisions.Select(x => (x.Id, Check: BaselineEligibility.ReadCustom(x, sourceId)))
            .Where(x => x.Check is not null).ToDictionary(x => x.Id, x => x.Check!);
    }

    public async Task<int> SourcePageAsync(Guid sourceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var ids = await db.IntelligenceSources.AsNoTracking().OrderBy(x => x.Key).Select(x => x.Id).ToArrayAsync(token);
        var index = Array.IndexOf(ids, sourceId);
        return index < 0 ? 1 : index / 50 + 1;
    }
}
