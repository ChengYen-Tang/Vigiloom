using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using System.Data;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal static class VendorRefreshStatus
{
    internal const string PublicationError = "Automatic intelligence activation failed.";
    internal static bool HasPublicationError(string? value) => value?.StartsWith(PublicationError, StringComparison.Ordinal) == true;
    internal sealed record PublicationSnapshot(Guid? Observed, Guid? Active, string Version, bool RetryPublication);

    internal static async Task<PublicationSnapshot> CaptureAsync(IDbContextFactory<SecurityAnalysisDbContext> contexts,
        IntelligenceSource source, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, token);
        return new(source.LastObservedRevisionId, active?.RevisionId, active is null ? "" : Convert.ToBase64String(active.Version), HasPublicationError(source.LastError));
    }

    internal static async Task PublishAsync(IDbContextFactory<SecurityAnalysisDbContext> contexts, IntelligencePublication publication, Guid sourceId, Guid observed,
        PublicationSnapshot before, CancellationToken token)
    {
        // Re-fetching the same upstream version must not undo an administrator's rollback.
        if (before.Active is not null && before.Observed == observed && !before.RetryPublication) return;
        try { await publication.ActivateLatestScheduledAsync(sourceId, observed, before.Active, before.Version, token); }
        catch (Exception error) when (error is DbException or DbUpdateException)
        {
            try
            {
                await MarkPublicationFailureAsync(contexts, sourceId, observed, before, token);
            }
            catch (Exception markError) when (markError is DbException or DbUpdateException) { /* The job still reports the original failure. */ }
            throw;
        }
    }

    internal static async Task MarkPublicationFailureAsync(IDbContextFactory<SecurityAnalysisDbContext> contexts, Guid sourceId, Guid observed,
        PublicationSnapshot before, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (source is { Enabled: true } && source.LastObservedRevisionId == observed && active?.RevisionId == before.Active &&
            (active is null ? before.Version.Length == 0 : Convert.ToBase64String(active.Version) == before.Version))
        {
            source.LastError = PublicationError;
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    internal static async Task<bool> UpdateAsync(IDbContextFactory<SecurityAnalysisDbContext> contexts,
        TimeProvider clock, IntelligenceSource expected, string? error, Guid? observed, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.IntelligenceSources.SingleOrDefaultAsync(x => x.Id == expected.Id, token);
        if (source is null || !source.Enabled || !source.Version.AsSpan().SequenceEqual(expected.Version)) return false;
        source.LastCheckedAt = clock.GetUtcNow().ToUniversalTime();
        source.LastError = HasPublicationError(source.LastError) ? error is null ? PublicationError : $"{PublicationError} {error}" : error;
        if (error is null)
        {
            source.LastSuccessfulCheckAt = source.LastCheckedAt;
            source.LastObservedRevisionId = observed;
        }
        try { await db.SaveChangesAsync(token); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
}
