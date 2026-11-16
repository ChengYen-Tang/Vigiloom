using System.ComponentModel;
using System.Security.Cryptography;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Agent.Core.Baseline;

namespace Vigiloom.Agent.Hosting.Baseline;

/// <summary>Periodically records reviewed read-only checks in the existing Agent queue.</summary>
internal sealed class BaselineWorker(AgentBaselineSettingsRuntime settings, AgentMetricSettingsRuntime metrics,
    SamplingBudgetCoordinator budget, IConfiguration configuration, TimeProvider clock, ILogger<BaselineWorker> logger,
    IMetricResourceMonitor? resourceMonitor = null,
    Func<BaselineCheck, CancellationToken, Task<(string OperatingSystem, BaselineObservation Result)>>? collectOverride = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentSettingsOwner? active = null;
        IAsyncDisposable? ownership = null;
        string? revision = null;
        var next = DateTimeOffset.MinValue;
        using var resources = resourceMonitor ?? new MetricResourceMonitor();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var directory = configuration["Vigiloom:Agent:StateDirectory"];
                    var server = configuration["Vigiloom:Agent:Server"];
                    if (!string.IsNullOrWhiteSpace(directory) && Uri.TryCreate(server, UriKind.Absolute, out var uri))
                    {
                        var vault = new IdentityVault(directory);
                        var owner = await AgentSettingsClient.GetOwnerAsync(vault, uri, stoppingToken);
                        if (owner != active)
                        {
                            if (ownership is not null) await ownership.DisposeAsync();
                            ownership = owner is null ? null : vault.AcquireBaselineOwnership();
                            active = owner;
                            revision = null;
                            next = DateTimeOffset.MinValue;
                        }
                        var saved = owner is null ? null : settings.ApplyForOwner(owner, clock.GetUtcNow());
                        var metricPolicy = owner is null ? null : metrics.ForOwner(owner);
                        if (owner is not null && saved is not null && metricPolicy is not null)
                        {
                            if (revision != saved.Snapshot.Revision) { revision = saved.Snapshot.Revision; next = DateTimeOffset.MinValue; }
                            if (clock.GetUtcNow() >= next)
                            {
                                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
                                bool queued;
                                try { queued = await CollectAsync(vault, owner, saved, resources, cancellation.Token); }
                                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                                {
                                    logger.LogWarning("Agent baseline collection timed out; it will be retried.");
                                    queued = false;
                                }
                                next = clock.GetUtcNow().AddSeconds(queued ? saved.Snapshot.ScanIntervalSeconds : 60);
                            }
                        }
                    }
                    else if (active is not null)
                    {
                        if (ownership is not null) await ownership.DisposeAsync();
                        ownership = null;
                        active = null;
                        revision = null;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                    UnauthorizedAccessException or CryptographicException or System.Data.Common.DbException or Win32Exception or PlatformNotSupportedException)
                {
                    logger.LogWarning("Agent baseline collection failed: {ErrorType}.", ex.GetType().Name);
                    next = clock.GetUtcNow().AddMinutes(1);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally { if (ownership is not null) await ownership.DisposeAsync(); }
    }

    private async Task<bool> CollectAsync(IdentityVault vault, AgentSettingsOwner owner, AgentSavedBaselineSettings saved,
        IMetricResourceMonitor resources, CancellationToken token)
    {
        if (saved.Snapshot.Checks.Count == 0) return true;
        await using var lease = await budget.AcquireAsync(token);
        var metricPolicy = metrics.ForOwner(owner);
        if (metricPolicy is null || settings.ForOwner(owner)?.Snapshot.Revision != saved.Snapshot.Revision) return false;
        budget.Cpu.SetAllowance(TimeSpan.FromMilliseconds(metricPolicy.CpuMillisecondsPerMinute));
        budget.Io.SetAllowance((ulong)metricPolicy.IoLimitMiBPerMinute * 1024 * 1024);
        if (budget.Cpu.Delay() > TimeSpan.Zero || budget.Io.Delay() > TimeSpan.Zero ||
            SamplingMemoryBudget.IsExceeded(resources.ReadWorkingSetBytes(), (long)metricPolicy.WorkingSetLimitMiB * 1024 * 1024)) return false;
        var cpu = resources.ReadCpuTime();
        var io = await resources.ReadIoAsync(token);
        try
        {
            var items = new List<BaselineResult>(saved.Snapshot.Checks.Count);
            foreach (var selection in saved.Snapshot.Checks)
            {
                var (system, observation) = collectOverride is null
                    ? await BaselineCollector.ReadAsync(selection.Check, token) : await collectOverride(selection.Check, token);
                items.Add(new(selection.SourceId, selection.RevisionId, selection.ActivationVersion, selection.Check.Id,
                    clock.GetUtcNow().ToUniversalTime(), system.Length <= 128 ? system : "unknown", observation.Outcome, observation.Evidence));
            }
            if (settings.ForOwner(owner)?.Snapshot.Revision != saved.Snapshot.Revision) return false;
            var now = clock.GetUtcNow().ToUniversalTime();
            var batch = new BaselineBatch(BaselineBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, items);
            var payload = BaselineBatchCodec.Serialize(batch);
            var queued = await new DurableBatchQueue(vault, owner.AgentId).EnqueueManyForOwnerAsync(
                [new QueuedBatch(batch.BatchId, now, items.Count, payload)], owner, token);
            if (queued is EnqueueResult.Queued or EnqueueResult.AlreadyQueued) return true;
            logger.LogWarning("Agent baseline result was not queued: {Status}.", queued);
            return false;
        }
        finally
        {
            try { budget.Io.Record(io, await resources.ReadIoAsync(CancellationToken.None)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException)
            { budget.Io.MarkUnknown(); }
            try { budget.Cpu.Record(cpu, resources.ReadCpuTime()); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            { budget.Cpu.MarkUnknown(); }
        }
    }
}
