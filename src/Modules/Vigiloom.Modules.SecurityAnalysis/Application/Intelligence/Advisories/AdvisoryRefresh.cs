using System.Data;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

public enum AdvisoryRefreshResult { Disabled, NotDue, Busy, Deferred, RateLimited, Staged, AwaitingActivation, Failed, Conflict }

/// <summary>Starts or resumes centrally scheduled GitHub-reviewed advisory imports.</summary>
public sealed class AdvisoryRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients,
    TimeProvider clock, IDataProtectionProvider protection)
{
    private static readonly DateOnly FirstWindow = new(1970, 1, 1);

    public async Task<AdvisoryRefreshResult> RunDueAsync(CancellationToken token)
    {
        var source = await EnsureSourceAsync(token);
        if (!source.Enabled) return AdvisoryRefreshResult.Disabled;
        AdvisoryImport? import;
        IntelligenceActivation? active;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            import = await db.AdvisoryImports.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id && x.Phase < 4, token);
            active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, token);
        }
        var store = new AdvisoryImportStore(contexts, clock);
        if (import is not null && (!import.SourceVersion.AsSpan().SequenceEqual(source.Version) ||
            import.BaseRevisionId != active?.RevisionId || (active is null ? import.BaseActivationVersion is not null :
                !active.Version.AsSpan().SequenceEqual(import.BaseActivationVersion))))
        {
            return await store.AbandonAsync(source.Id, import.Id, import.Version, token)
                ? AdvisoryRefreshResult.Deferred : AdvisoryRefreshResult.Conflict;
        }
        if (import?.FailureCode is "invalid-response" or "cursor-rejected" or "limit-exceeded" or "invalid-base" or "credential-unavailable") return AdvisoryRefreshResult.Failed;
        if (import is null)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var candidate = await db.AdvisoryImports.AsNoTracking().Where(x => x.SourceId == source.Id && x.Phase == 4 &&
                    x.ResultRevisionId != null && x.BaseRevisionId == (active == null ? null : active.RevisionId))
                .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.RowId).FirstOrDefaultAsync(token);
            var now = clock.GetUtcNow();
            if (candidate is not null && candidate.ResultSourceVersion is not null &&
                candidate.ResultSourceVersion.AsSpan().SequenceEqual(source.Version) && source.LastSuccessfulCheckAt is { } checkedAt &&
                checkedAt <= now && checkedAt >= now.AddSeconds(-source.StaleAfterSeconds) &&
                (active is null ? candidate.BaseActivationVersion is null :
                active.Version.AsSpan().SequenceEqual(candidate.BaseActivationVersion)) &&
                await AdvisoryRevisionEligibility.IsEligibleAsync(db, source.Id, candidate.ResultRevisionId!.Value, token))
                return AdvisoryRefreshResult.AwaitingActivation;
            if (!IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return AdvisoryRefreshResult.NotDue;
            var end = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-1);
            var start = FirstWindow;
            if (active is not null)
            {
                var previousEnd = await db.AdvisoryImports.AsNoTracking().Where(x => x.SourceId == source.Id && x.ResultRevisionId == active.RevisionId && x.Phase == 4)
                    .Select(x => (DateOnly?)x.WindowEnd).SingleOrDefaultAsync(token);
                if (previousEnd is null) return AdvisoryRefreshResult.Failed;
                if (previousEnd >= end) return AdvisoryRefreshResult.NotDue;
                start = previousEnd.Value.DayNumber - FirstWindow.DayNumber < 7 ? FirstWindow : previousEnd.Value.AddDays(-7);
            }
            if (end < start) return AdvisoryRefreshResult.NotDue;
            import = await store.BeginAsync(source.Id, start, end, token, verifyBase: true,
                expectedBaseId: active?.RevisionId, expectedBaseVersion: active?.Version);
            if (import is null) return AdvisoryRefreshResult.Conflict;
        }
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var current = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.Id, token);
            if (current is null || !current.Enabled || !current.Version.AsSpan().SequenceEqual(import.SourceVersion)) return AdvisoryRefreshResult.Conflict;
            source = current;
        }
        string? credential = null;
        if (source.ProtectedCredential is not null)
        {
            try { credential = protection.CreateProtector(AdvisoryCredentials.ProtectionPurpose).Unprotect(source.ProtectedCredential); }
            catch (Exception error) when (error is CryptographicException or FormatException)
            {
                await store.RecordFailureAsync(source.Id, import.Id, import.Version, "credential-unavailable", token);
                return AdvisoryRefreshResult.Failed;
            }
        }
        var result = await new AdvisoryImportRunner(contexts, clients, clock).RunSliceAsync(source.Id, import.Id, credential, token, reconcile: true);
        if (result.Status != AdvisoryRunResult.Reconciled)
            return result.Status switch
            {
                AdvisoryRunResult.Busy => AdvisoryRefreshResult.Busy,
                AdvisoryRunResult.RateLimited => AdvisoryRefreshResult.RateLimited,
                AdvisoryRunResult.Deferred => AdvisoryRefreshResult.Deferred,
                AdvisoryRunResult.Conflict => AdvisoryRefreshResult.Conflict,
                _ => AdvisoryRefreshResult.Failed
            };
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var reconciled = await db.AdvisoryImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == result.ImportId && x.SourceId == source.Id, token);
            if (reconciled is null) return AdvisoryRefreshResult.Conflict;
            var built = await new AdvisoryRevisionBuilder(contexts, clock).StageAsync(source.Id, reconciled.Id, reconciled.Version, token);
            if (built.Status is AdvisoryBuildStatus.LimitExceeded or AdvisoryBuildStatus.InvalidBase or AdvisoryBuildStatus.InvalidContent)
                await store.RecordFailureAsync(source.Id, reconciled.Id, reconciled.Version,
                    built.Status switch { AdvisoryBuildStatus.LimitExceeded => "limit-exceeded", AdvisoryBuildStatus.InvalidBase => "invalid-base", _ => "invalid-response" }, token);
            return built.Status switch
            {
                AdvisoryBuildStatus.Staged => AdvisoryRefreshResult.Staged,
                AdvisoryBuildStatus.AlreadyStaged => AdvisoryRefreshResult.AwaitingActivation,
                AdvisoryBuildStatus.Conflict => AdvisoryRefreshResult.Conflict,
                _ => AdvisoryRefreshResult.Failed
            };
        }
    }

    private async Task<IntelligenceSource> EnsureSourceAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlRaw("SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = N'github-reviewed'").SingleOrDefaultAsync(token);
        if (source is null)
        {
            source = new IntelligenceSource { Key = "github-reviewed", Enabled = true, MaximumBytes = 1_073_741_824, MaximumRecords = 100_000 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return source;
    }
}
