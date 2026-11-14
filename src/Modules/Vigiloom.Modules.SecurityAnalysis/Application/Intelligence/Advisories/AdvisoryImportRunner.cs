using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

internal enum AdvisoryRunResult { Busy, Deferred, RateLimited, AwaitingReconciliation, Conflict, Invalid, CursorRejected, Failed }

/// <summary>Processes a bounded slice of an existing advisory import; never activates partial data.</summary>
internal sealed class AdvisoryImportRunner(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IHttpClientFactory clients, TimeProvider clock)
{
    private const int MaxPagesPerSlice = 200;
    private static readonly TimeSpan SliceDuration = TimeSpan.FromMinutes(15);

    internal async Task<AdvisoryRunResult> RunSliceAsync(Guid sourceId, Guid importId, string? tokenValue, CancellationToken token,
        TimeSpan? duration = null)
    {
        if (sourceId == Guid.Empty || importId == Guid.Empty) throw new ArgumentException("Invalid advisory source or import.");
        var timeLimit = duration ?? SliceDuration;
        if (timeLimit <= TimeSpan.Zero || timeLimit > SliceDuration) throw new ArgumentOutOfRangeException(nameof(duration));
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, sourceId, token);
        if (lease is null) return AdvisoryRunResult.Busy;
        var elapsed = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(timeLimit);
        var store = new AdvisoryImportStore(contexts, clock);
        using var http = clients.CreateClient(IntelligenceHttp.AdvisoryClientName);
        var client = new AdvisoryClient(http, clock);
        for (var pageCount = 0; pageCount < MaxPagesPerSlice && elapsed.Elapsed < timeLimit; pageCount++)
        {
            AdvisoryImport? import;
            try
            {
                await using var db = await contexts.CreateDbContextAsync(budget.Token);
                import = await db.AdvisoryImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == importId && x.SourceId == sourceId, budget.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
            {
                return AdvisoryRunResult.Deferred;
            }
            if (import is null) return AdvisoryRunResult.Conflict;
            if (import.Phase == 3) return AdvisoryRunResult.AwaitingReconciliation;
            if (import.Phase is not (1 or 2)) return AdvisoryRunResult.Conflict;
            if (import.NotBefore is { } notBefore && notBefore > clock.GetUtcNow()) return AdvisoryRunResult.RateLimited;
            AdvisoryPage page;
            try
            {
                page = await client.FetchAsync(import.WindowStart, import.WindowEnd, import.Phase == 2,
                    import.Cursor, tokenValue, budget.Token);
            }
            catch (AdvisoryRateLimitException error)
            {
                using var persistenceBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                persistenceBudget.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    return await store.DeferAsync(sourceId, importId, import.Version, error.RetryAt, persistenceBudget.Token)
                        ? AdvisoryRunResult.RateLimited : AdvisoryRunResult.Conflict;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && persistenceBudget.IsCancellationRequested)
                {
                    return AdvisoryRunResult.Failed;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
            {
                return AdvisoryRunResult.Deferred;
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.UnprocessableEntity && import.Cursor is not null)
            {
                return AdvisoryRunResult.CursorRejected;
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or OperationCanceledException && !token.IsCancellationRequested)
            {
                return AdvisoryRunResult.Failed;
            }
            AdvisoryStageResult result;
            try { result = await store.StageAsync(sourceId, importId, import.Version, import.Cursor, import.Phase == 2, page, budget.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
            {
                return AdvisoryRunResult.Deferred;
            }
            if (result == AdvisoryStageResult.Conflict) return AdvisoryRunResult.Conflict;
            if (result is AdvisoryStageResult.Invalid or AdvisoryStageResult.LimitExceeded) return AdvisoryRunResult.Invalid;
        }
        return AdvisoryRunResult.Deferred;
    }
}
