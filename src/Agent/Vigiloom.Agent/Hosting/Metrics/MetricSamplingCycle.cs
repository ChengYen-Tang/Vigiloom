using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Serializes one owner's sampling and queueing. A caller must not overlap cycles.</summary>
internal sealed class MetricSamplingCycle(AgentSettingsOwner owner, DurableBatchQueue queue, TimeProvider clock, IHostMetricCollector? collectorOverride = null) : IDisposable
{
    private readonly IHostMetricCollector collector = collectorOverride ?? new HostMetricCollector(clock);
    private readonly MetricSamplingSchedule schedule = new(clock);
    private readonly MetricSamplePublisher publisher = new(queue, owner);
    private bool processDeferred;
    public bool HasPending => publisher.HasPending;
    public int PendingBatches => publisher.PendingBatches;
    public long PendingRecords => publisher.PendingRecords;
    public bool PendingOutcomeUnknown => publisher.PendingOutcomeUnknown;
    public int KnownUnqueuedBatches => publisher.KnownUnqueuedBatches;
    public long KnownUnqueuedRecords => publisher.KnownUnqueuedRecords;

    /// <summary>Try to persist the current pending sample without starting another collection.</summary>
    public Task<EnqueueResult>? FlushPendingAsync(CancellationToken token) => publisher.HasPending ? publisher.FlushAsync(token) : null;

    /// <summary>Give the independent sender time to free queue space while the shutdown deadline allows.</summary>
    public async Task<EnqueueResult> FlushForShutdownAsync(CancellationToken token)
    {
        while (publisher.HasPending)
        {
            var status = await publisher.FlushAsync(token);
            if (status == EnqueueResult.Full)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                continue;
            }
            if (status != EnqueueResult.Queued) return status;
        }
        return EnqueueResult.Queued;
    }

    public async Task<MetricCycleResult> RunAsync(AgentMetricSettings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (publisher.HasPending)
        {
            var queued = await publisher.FlushAsync(token);
            if (queued != EnqueueResult.Queued) return new(queued, TimeSpan.FromSeconds(5), []);
        }

        var due = schedule.GetDue(settings);
        if (!due.Host && !due.Process) return new(null, due.Delay, []);
        var unavailable = new List<string>();
        EnqueueResult? status = null;
        if (processDeferred && due.Process)
        {
            processDeferred = false;
            schedule.MarkStarted(host: false, process: true);
            var processes = collector.CollectProcesses(TimeSpan.FromSeconds(settings.ProcessIntervalSeconds * 2), token, TimeSpan.FromMilliseconds(settings.ProcessCollectionBudgetMs));
            unavailable.AddRange(processes.UnavailableSources);
            status = await PublishAsync(processes, token);
            if (status is not (null or EnqueueResult.Queued)) return new(status, TimeSpan.FromSeconds(5), unavailable);
            due = due with { Process = false };
        }
        if (due.Host)
        {
            schedule.MarkStarted(host: true, process: false);
            // Preserve process priority if host collection or queueing fails.
            processDeferred = due.Process;
            var host = await collector.CollectAsync(TimeSpan.FromSeconds(settings.HostIntervalSeconds * 2), token, includeProcesses: false);
            unavailable.AddRange(host.UnavailableSources);
            status = await PublishAsync(host, token);
            if (status is not (null or EnqueueResult.Queued))
            {
                return new(status, TimeSpan.FromSeconds(5), unavailable);
            }
            processDeferred = false;
        }
        if (due.Process)
        {
            schedule.MarkStarted(host: false, process: true);
            var processes = collector.CollectProcesses(TimeSpan.FromSeconds(settings.ProcessIntervalSeconds * 2), token, TimeSpan.FromMilliseconds(settings.ProcessCollectionBudgetMs));
            unavailable.AddRange(processes.UnavailableSources);
            var processStatus = await PublishAsync(processes, token);
            if (processStatus is not null) status = processStatus;
        }
        return new(status, status is null or EnqueueResult.Queued ? schedule.GetDue(settings).Delay : TimeSpan.FromSeconds(5), unavailable);
    }

    private async Task<EnqueueResult?> PublishAsync(HostMetricCollection collection, CancellationToken token)
    {
        var points = new List<AgentMetricPoint>(collection.Points);
        // The absence of a numeric point must remain distinguishable from a
        // healthy reading. Keep the source marker in the same transport path.
        var observedAt = clock.GetUtcNow();
        foreach (var source in collection.UnavailableSources.Distinct(StringComparer.Ordinal))
            points.Add(new(Guid.CreateVersion7(), observedAt, AgentMetricKind.CollectionUnavailable, "agent.collector", source, 1));
        if (points.Count == 0) return null;
        publisher.Prepare(points, clock.GetUtcNow());
        return await publisher.FlushAsync(token);
    }

    public void Dispose() => collector.Dispose();
}

internal sealed record MetricCycleResult(EnqueueResult? Status, TimeSpan Delay, IReadOnlyList<string> UnavailableSources);
