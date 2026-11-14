using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

internal enum AdvisoryRunResult { Busy, Deferred, RateLimited, AwaitingReconciliation, Conflict, Invalid, CursorRejected, Failed }

internal sealed record AdvisoryRunOutcome(AdvisoryRunResult Status, Guid ImportId);

/// <summary>Processes a bounded slice of an existing advisory import; never activates partial data.</summary>
internal sealed class AdvisoryImportRunner(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IHttpClientFactory clients, TimeProvider clock)
{
    private const int MaxPagesPerSlice = 200;
    private static readonly TimeSpan SliceDuration = TimeSpan.FromMinutes(15);

    internal async Task<AdvisoryRunOutcome> RunSliceAsync(Guid sourceId, Guid importId, string? tokenValue, CancellationToken token,
        TimeSpan? duration = null)
    {
        if (sourceId == Guid.Empty || importId == Guid.Empty) throw new ArgumentException("Invalid advisory source or import.");
        var timeLimit = duration ?? SliceDuration;
        if (timeLimit <= TimeSpan.Zero || timeLimit > SliceDuration) throw new ArgumentOutOfRangeException(nameof(duration));
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, sourceId, token);
        if (lease is null) return new(AdvisoryRunResult.Busy, importId);
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
                return new(AdvisoryRunResult.Deferred, importId);
            }
            if (import is null) return new(AdvisoryRunResult.Conflict, importId);
            if (import.Phase == 5 && import.SuccessorId is { } successor)
            {
                importId = successor;
                continue;
            }
            if (import.Phase == 3) return new(AdvisoryRunResult.AwaitingReconciliation, importId);
            if (import.Phase is not (1 or 2)) return new(AdvisoryRunResult.Conflict, importId);
            if (import.NotBefore is { } notBefore && notBefore > clock.GetUtcNow()) return new(AdvisoryRunResult.RateLimited, importId);
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
                    return new(await store.DeferAsync(sourceId, importId, import.Version, error.RetryAt, persistenceBudget.Token)
                        ? AdvisoryRunResult.RateLimited : AdvisoryRunResult.Conflict, importId);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && persistenceBudget.IsCancellationRequested)
                {
                    return new(AdvisoryRunResult.Failed, importId);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
            {
                return new(AdvisoryRunResult.Deferred, importId);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.UnprocessableEntity && import.Cursor is not null)
            {
                try
                {
                    var restarted = await store.RestartAsync(sourceId, importId, import.Version, budget.Token);
                    if (restarted is null) return new(AdvisoryRunResult.CursorRejected, importId);
                    importId = restarted.Id;
                    continue;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
                {
                    return new(AdvisoryRunResult.Deferred, importId);
                }
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or OperationCanceledException && !token.IsCancellationRequested)
            {
                return new(AdvisoryRunResult.Failed, importId);
            }
            AdvisoryStageResult result;
            try { result = await store.StageAsync(sourceId, importId, import.Version, import.Cursor, import.Phase == 2, page, budget.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
            {
                return new(AdvisoryRunResult.Deferred, importId);
            }
            if (result == AdvisoryStageResult.Conflict) return new(AdvisoryRunResult.Conflict, importId);
            if (result is AdvisoryStageResult.Invalid or AdvisoryStageResult.LimitExceeded) return new(AdvisoryRunResult.Invalid, importId);
        }
        return new(AdvisoryRunResult.Deferred, importId);
    }
}
