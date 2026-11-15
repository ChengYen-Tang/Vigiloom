using System.Collections.Concurrent;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record CatalogBatchResult(IReadOnlyList<WindowsCatalogEvidence> Evidence, bool Complete);
internal sealed class CatalogRunBudget
{
    private int remaining = 8;
    private TimeSpan elapsed;
    internal bool Exhausted => remaining <= 0 || elapsed >= TimeSpan.FromSeconds(20);
    internal TimeSpan RemainingTime => TimeSpan.FromSeconds(20) - elapsed;
    internal void Record(TimeSpan duration) { remaining--; elapsed += duration; }
}

/// <summary>Shares bounded Catalog observations across host assessments within one worker process.</summary>
public sealed class MicrosoftCatalogCache : IDisposable
{
    private const int MaximumEntries = 4096;
    private const int MaximumRequestsPerBatch = 8;
    private const int MaximumRelationsPerBatch = 50_000;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<(string Product, string Kb), Entry> entries = new();
    private readonly Func<string, string, CancellationToken, Task<CatalogRelationResult>> fetch;
    private readonly TimeProvider clock;
    private readonly TimeSpan batchTimeout;
    private DateTimeOffset retryAfter;
    internal long NextHostCursor { get; set; }

    public MicrosoftCatalogCache(IHttpClientFactory clients, TimeProvider clock)
        : this(new MicrosoftCatalogRelations(clients).GetAsync, clock) { }

    internal MicrosoftCatalogCache(Func<string, string, CancellationToken, Task<CatalogRelationResult>> fetch, TimeProvider clock, TimeSpan? batchTimeout = null)
    { this.fetch = fetch; this.clock = clock; this.batchTimeout = batchTimeout ?? TimeSpan.FromSeconds(20); }

    internal async Task<CatalogBatchResult> GetBatchAsync(string product, IReadOnlyCollection<string> kbs, CancellationToken token, CatalogRunBudget? budget = null)
    {
        if (MicrosoftCatalogRelations.Scope(product) is null) return new([], false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(batchTimeout);
        var result = new List<WindowsCatalogEvidence>();
        var complete = true;
        var fetched = 0;
        var relationCount = 0;
        foreach (var kb in kbs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var (entry, requested) = await GetAsync(product, kb, fetched < MaximumRequestsPerBatch, deadline.Token, token, budget);
                if (requested) fetched++;
                if (entry?.Result is not { Status: CatalogRelationStatus.Ready, UpdateId: { } updateId, SupersededKbs: { } relations })
                { complete = false; continue; }
                if (relations.Count > MaximumRelationsPerBatch - relationCount) { complete = false; break; }
                relationCount += relations.Count;
                result.Add(new(product, kb, updateId, entry.ObservedAt, relations));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { complete = false; break; }
        }
        return new(result, complete);
    }

    private async Task<(Entry? Entry, bool Requested)> GetAsync(string product, string kb, bool allowRequest, CancellationToken token, CancellationToken callerToken, CatalogRunBudget? budget)
    {
        // Server and Core use the same update Catalog product for each supported release.
        const string core = " (Server Core installation)";
        var scope = product.EndsWith(core, StringComparison.Ordinal) ? product[..^core.Length] : product;
        var key = (scope, kb);
        if (Fresh(key, clock.GetUtcNow()) is { } hit) return (hit, false);
        if (!allowRequest) return (null, false);
        await gate.WaitAsync(token);
        try
        {
            var now = clock.GetUtcNow();
            if (Fresh(key, now) is { } cached) return (cached, false);
            if (now < retryAfter || budget?.Exhausted == true) return (null, false);
            CatalogRelationResult value;
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (budget is not null) requestDeadline.CancelAfter(budget.RemainingTime);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { value = await fetch(scope, kb, requestDeadline.Token); }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested && requestDeadline.IsCancellationRequested)
            {
                now = clock.GetUtcNow();
                // Outlive the 15-minute reassessment interval so a slow KB cannot starve later entries.
                Store(key, new(new(CatalogRelationStatus.Unavailable), now, now.AddHours(2)));
                // A slow upstream should not cost every host another full network timeout.
                retryAfter = now.AddMinutes(2);
                throw;
            }
            finally { budget?.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started)); }
            now = clock.GetUtcNow();
            var entry = new Entry(value, now, now.Add(value.Status == CatalogRelationStatus.Ready ? TimeSpan.FromHours(23) : TimeSpan.FromHours(2)));
            Store(key, entry);
            return (entry, true);
        }
        finally { gate.Release(); }
    }

    private Entry? Fresh((string Product, string Kb) key, DateTimeOffset now) =>
        entries.TryGetValue(key, out var value) && value.ExpiresAt > now && value.ObservedAt <= now ? value : null;

    private void Store((string Product, string Kb) key, Entry entry)
    {
        if (entries.Count >= MaximumEntries && !entries.ContainsKey(key))
            entries.TryRemove(entries.MinBy(x => x.Value.ExpiresAt).Key, out _);
        entries[key] = entry;
    }

    public void Dispose() => gate.Dispose();
    private sealed record Entry(CatalogRelationResult Result, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt);
}
