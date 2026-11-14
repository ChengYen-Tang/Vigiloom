using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

internal enum AdvisoryStageResult { Stored, Conflict, Invalid, LimitExceeded }

/// <summary>Stores each verified page and its cursor atomically without publishing an incomplete import.</summary>
internal sealed class AdvisoryImportStore(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    internal async Task<AdvisoryImport?> BeginAsync(Guid sourceId, DateOnly from, DateOnly to, CancellationToken token,
        bool verifyBase = false, Guid? expectedBaseId = null, byte[]? expectedBaseVersion = null)
    {
        if (sourceId == Guid.Empty || to < from) throw new ArgumentException("Invalid advisory source or window.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await LockSourceAsync(db, sourceId, token);
        if (source is null || !source.Enabled || source.Key != "github-reviewed") return null;
        if (await db.AdvisoryImports.AnyAsync(x => x.SourceId == sourceId && x.Phase < 4, token)) return null;
        var active = await db.IntelligenceActivations.AsNoTracking().Where(x => x.SourceId == sourceId)
            .Select(x => new { x.RevisionId, x.Version }).SingleOrDefaultAsync(token);
        if (verifyBase && (active?.RevisionId != expectedBaseId || (active is null ? expectedBaseVersion is not null :
            !active.Version.AsSpan().SequenceEqual(expectedBaseVersion)))) return null;
        var now = clock.GetUtcNow().ToUniversalTime();
        var deferred = await db.AdvisoryImports.AsNoTracking().Where(x => x.SourceId == sourceId && x.NotBefore > now)
            .OrderByDescending(x => x.NotBefore).ThenByDescending(x => x.RateLimitAttempts)
            .Select(x => new { x.NotBefore, x.RateLimitAttempts }).FirstOrDefaultAsync(token);
        var generation = new AdvisoryImport
        {
            SourceId = sourceId, BaseRevisionId = active?.RevisionId, BaseActivationVersion = active?.Version, SourceVersion = source.Version.ToArray(),
            WindowStart = from, WindowEnd = to, StartedAt = now, NotBefore = deferred?.NotBefore,
            RateLimitAttempts = deferred?.RateLimitAttempts ?? 0
        };
        db.AdvisoryImports.Add(generation);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return generation;
    }

    internal async Task<AdvisoryStageResult> StageAsync(Guid sourceId, Guid importId, byte[] expectedVersion,
        string? requestedCursor, bool withdrawn, AdvisoryPage page, CancellationToken token)
    {
        if (sourceId == Guid.Empty || importId == Guid.Empty || expectedVersion.Length != 8 || page.Records.Count > 100 ||
            page.RawBytes.Length is < 1 or > AdvisoryClient.MaximumPageBytes ||
            page.NextCursor is not null && !AdvisoryLinks.ValidCursor(page.NextCursor)) return AdvisoryStageResult.Invalid;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await LockSourceAsync(db, sourceId, token);
        if (source is null || !source.Enabled || source.Key != "github-reviewed") return AdvisoryStageResult.Conflict;
        var import = await db.AdvisoryImports
            .FromSqlInterpolated($"SELECT * FROM [security].[AdvisoryImports] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {importId} AND [SourceId] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (import is null || !import.Version.AsSpan().SequenceEqual(expectedVersion) || !import.SourceVersion.AsSpan().SequenceEqual(source.Version) ||
            (import.Phase == 3 ? import.ReconciliationPhase : import.Phase) != (withdrawn ? 2 : 1) || import.Cursor != requestedCursor || page.From != import.WindowStart || page.To != import.WindowEnd ||
            page.Withdrawn != withdrawn || page.RequestedCursor != requestedCursor) return AdvisoryStageResult.Conflict;
        var reconciliation = import.Phase == 3;
        var active = await db.IntelligenceActivations.AsNoTracking().Where(x => x.SourceId == sourceId)
            .Select(x => new { x.RevisionId, x.Version }).SingleOrDefaultAsync(token);
        if (active?.RevisionId != import.BaseRevisionId ||
            (active is null ? import.BaseActivationVersion is not null : !active.Version.AsSpan().SequenceEqual(import.BaseActivationVersion)))
            return AdvisoryStageResult.Conflict;
        if (page.NextCursor is not null && (page.NextCursor == requestedCursor || await db.AdvisoryImportPages.AnyAsync(
                x => x.ImportId == importId && x.Reconciliation == reconciliation && x.Phase == (withdrawn ? 2 : 1) && x.NextCursor == page.NextCursor, token)))
            return AdvisoryStageResult.Invalid;
        if (import.PageCount >= 100_000 || import.TotalBytes + page.RawBytes.Length > 1_073_741_824L)
            return AdvisoryStageResult.LimitExceeded;
        var ids = page.Records.Select(x => x.Id).ToArray();
        var existing = await db.AdvisoryImportEntries.Where(x => x.ImportId == importId && x.Reconciliation == reconciliation && ids.Contains(x.AdvisoryId))
            .ToDictionaryAsync(x => x.AdvisoryId, StringComparer.Ordinal, token);
        var newCount = page.Records.Count(x => !existing.ContainsKey(x.Id));
        if ((long)(reconciliation ? import.ReconciliationRecordCount : import.RecordCount) + newCount > 100_000) return AdvisoryStageResult.LimitExceeded;
        var storedPage = new AdvisoryImportPage
        {
            ImportId = importId, Number = import.PageCount + 1, Phase = withdrawn ? 2 : 1, Reconciliation = reconciliation,
            Digest = SHA256.HashData(page.RawBytes), Payload = page.RawBytes, NextCursor = page.NextCursor
        };
        db.AdvisoryImportPages.Add(storedPage);
        await db.SaveChangesAsync(token);
        for (var i = 0; i < page.Records.Count; i++)
        {
            var record = page.Records[i];
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(record.OriginalJson));
            if (existing.TryGetValue(record.Id, out var previous))
            {
                if (record.UpdatedAt < previous.UpdatedAt) continue;
                if (record.UpdatedAt == previous.UpdatedAt && !digest.AsSpan().SequenceEqual(previous.Digest)) return AdvisoryStageResult.Invalid;
                if (record.UpdatedAt == previous.UpdatedAt) continue;
                previous.PageRowId = storedPage.RowId;
                previous.Ordinal = i;
                previous.Digest = digest;
                previous.UpdatedAt = record.UpdatedAt;
                previous.WithdrawnAt = record.WithdrawnAt;
            }
            else
                db.AdvisoryImportEntries.Add(new AdvisoryImportEntry
                {
                    ImportId = importId, PageRowId = storedPage.RowId, Ordinal = i, AdvisoryId = record.Id, Reconciliation = reconciliation,
                    Digest = digest, UpdatedAt = record.UpdatedAt, WithdrawnAt = record.WithdrawnAt
                });
        }
        import.PageCount++;
        if (reconciliation) import.ReconciliationRecordCount += newCount;
        else import.RecordCount += newCount;
        import.TotalBytes += page.RawBytes.Length;
        import.Cursor = page.NextCursor;
        if (page.NextCursor is null)
        {
            if (reconciliation) import.ReconciliationPhase++;
            else import.Phase++;
        }
        import.NotBefore = null;
        import.RateLimitAttempts = 0;
        import.FailureCode = null;
        await db.SaveChangesAsync(token);
        if (reconciliation && import.ReconciliationPhase == 3)
        {
            var original = db.AdvisoryImportEntries.Where(x => x.ImportId == importId && !x.Reconciliation);
            var verified = db.AdvisoryImportEntries.Where(x => x.ImportId == importId && x.Reconciliation);
            var differs = import.RecordCount != import.ReconciliationRecordCount || await original.AnyAsync(a =>
                !verified.Any(b => b.AdvisoryId == a.AdvisoryId && b.Digest == a.Digest && b.UpdatedAt == a.UpdatedAt && b.WithdrawnAt == a.WithdrawnAt), token);
            if (differs) import.ReconciliationPhase = 4;
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return AdvisoryStageResult.Stored;
    }

    internal async Task<bool> DeferAsync(Guid sourceId, Guid importId, DateTimeOffset? retryAt, CancellationToken token)
    {
        if (sourceId == Guid.Empty || importId == Guid.Empty) return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await LockSourceAsync(db, sourceId, token);
        if (source is null || source.Key != "github-reviewed") return false;
        var import = await db.AdvisoryImports
            .FromSqlInterpolated($"SELECT * FROM [security].[AdvisoryImports] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {importId} AND [SourceId] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (import is null) return false;
        import.RateLimitAttempts = Math.Min(import.RateLimitAttempts + 1, 20);
        var minimum = clock.GetUtcNow().AddMinutes(1 << Math.Min(import.RateLimitAttempts - 1, 6));
        var next = retryAt is { } limit && limit > minimum ? limit.ToUniversalTime() : minimum;
        import.NotBefore = import.NotBefore is { } existing && existing > next ? existing : next;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    internal async Task<AdvisoryImport?> RestartAsync(Guid sourceId, Guid importId, byte[] expectedVersion, CancellationToken token)
    {
        if (sourceId == Guid.Empty || importId == Guid.Empty || expectedVersion.Length != 8) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await LockSourceAsync(db, sourceId, token);
        if (source is null || !source.Enabled || source.Key != "github-reviewed") return null;
        var old = await db.AdvisoryImports
            .FromSqlInterpolated($"SELECT * FROM [security].[AdvisoryImports] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {importId} AND [SourceId] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (old is null || old.Phase is not (1 or 2 or 3) || old.Cursor is null || old.RestartCount >= 3 ||
            !old.Version.AsSpan().SequenceEqual(expectedVersion) || !old.SourceVersion.AsSpan().SequenceEqual(source.Version)) return null;
        var active = await db.IntelligenceActivations.AsNoTracking().Where(x => x.SourceId == sourceId)
            .Select(x => new { x.RevisionId, x.Version }).SingleOrDefaultAsync(token);
        if (active?.RevisionId != old.BaseRevisionId ||
            (active is null ? old.BaseActivationVersion is not null : !active.Version.AsSpan().SequenceEqual(old.BaseActivationVersion))) return null;
        var next = new AdvisoryImport
        {
            SourceId = sourceId, BaseRevisionId = old.BaseRevisionId, BaseActivationVersion = old.BaseActivationVersion,
            SourceVersion = old.SourceVersion, WindowStart = old.WindowStart, WindowEnd = old.WindowEnd,
            StartedAt = clock.GetUtcNow().ToUniversalTime(), RestartCount = old.RestartCount + 1
        };
        old.Phase = 5;
        old.SuccessorId = next.Id;
        await db.SaveChangesAsync(token);
        db.AdvisoryImports.Add(next);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return next;
    }

    internal async Task<bool> AbandonAsync(Guid sourceId, Guid importId, byte[] expectedVersion, CancellationToken token)
    {
        if (sourceId == Guid.Empty || importId == Guid.Empty || expectedVersion.Length != 8) return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (await LockSourceAsync(db, sourceId, token) is null) return false;
        var import = await db.AdvisoryImports
            .FromSqlInterpolated($"SELECT * FROM [security].[AdvisoryImports] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {importId} AND [SourceId] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (import is null || import.Phase is not (1 or 2 or 3) || !import.Version.AsSpan().SequenceEqual(expectedVersion)) return false;
        import.Phase = 5;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    internal async Task<bool> RecordFailureAsync(Guid sourceId, Guid importId, byte[] expectedVersion, string code, CancellationToken token)
    {
        if (sourceId == Guid.Empty || importId == Guid.Empty || expectedVersion.Length != 8 ||
            code is not ("fetch-failed" or "invalid-response" or "request-timeout" or "cursor-rejected" or "limit-exceeded" or "invalid-base")) return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (await LockSourceAsync(db, sourceId, token) is null) return false;
        var import = await db.AdvisoryImports
            .FromSqlInterpolated($"SELECT * FROM [security].[AdvisoryImports] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {importId} AND [SourceId] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (import is null || import.Phase is not (1 or 2 or 3) || !import.Version.AsSpan().SequenceEqual(expectedVersion)) return false;
        import.FailureCode = code;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    private static Task<IntelligenceSource?> LockSourceAsync(SecurityAnalysisDbContext db, Guid id, CancellationToken token) =>
        db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
            .SingleOrDefaultAsync(token);
}
