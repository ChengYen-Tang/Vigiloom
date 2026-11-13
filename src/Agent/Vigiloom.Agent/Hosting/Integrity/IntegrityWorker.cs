using System.ComponentModel;
using System.Security.Cryptography;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Hosting.Integrity;

/// <summary>Serializes scope scans and resumes their durable generation before starting another.</summary>
internal sealed class IntegrityWorker(IntegrityStatusClient statusClient, AgentIntegritySettingsRuntime settings,
    AgentMetricSettingsRuntime metricSettings, SamplingBudgetCoordinator budget, IConfiguration configuration,
    TimeProvider clock, ILogger<IntegrityWorker> logger, IMetricResourceMonitor? resourceMonitor = null,
    Func<IntegrityScope, CancellationToken, Task<IntegrityReadResult>>? collectOverride = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedule = new IntegritySchedule(clock);
        var marked = new Dictionary<Guid, long>();
        var cycle = new IntegrityCycle(statusClient, clock);
        AgentSettingsOwner? active = null;
        IAsyncDisposable? ownership = null;
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
                            ownership = null;
                            active = null;
                            schedule.Reset();
                            marked.Clear();
                            ownership = owner is null ? null : vault.AcquireIntegrityOwnership();
                            active = owner;
                        }
                        if (owner is not null)
                        {
                            // A prior generation must be reconciled even if every scope was retired.
                            var pending = await cycle.ResumeOutstandingAsync(vault, uri, owner, stoppingToken);
                            var saved = settings.ApplyForOwner(owner, clock.GetUtcNow());
                            if (saved is not null)
                            {
                                var queue = new DurableBatchQueue(vault, owner.AgentId);
                                var scopes = saved.Snapshot.Scopes;
                                var enabled = scopes.Where(x => x.Enabled).ToArray();
                                var present = enabled.Select(x => x.Id).ToHashSet();
                                foreach (var id in marked.Keys.Where(id => !present.Contains(id)).ToArray()) marked.Remove(id);
                                foreach (var scope in enabled)
                                {
                                    if (marked.TryGetValue(scope.Id, out var revision) && revision == scope.Revision) continue;
                                    await queue.MarkIntegrityGapAsync(owner, scope.Id, scope.Revision, stoppingToken);
                                    marked[scope.Id] = scope.Revision;
                                }
                                if (pending is null && schedule.Next(scopes) is { } selected)
                                {
                                    try
                                    {
                                        var result = await cycle.RunAsync(vault, uri, owner, saved, selected, CollectUnderBudget, stoppingToken,
                                            (action, token) => WithBudget(action, token, false));
                                        if (result.Kind == IntegrityCycleKind.Queued) schedule.Finish(selected);
                                        else schedule.Defer(selected, PollInterval);
                                        if (result.Kind is IntegrityCycleKind.TooLarge or IntegrityCycleKind.QueueFull)
                                            logger.LogWarning("Agent integrity scope {ScopeId} could not be queued: {Reason}.", selected.Id, result.Kind);
                                    }
                                    catch (IntegrityDeferredException) { schedule.Defer(selected, PollInterval); }
                                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                                        UnauthorizedAccessException or CryptographicException or System.Data.Common.DbException or Win32Exception)
                                    {
                                        schedule.Defer(selected, PollInterval);
                                        logger.LogWarning("Agent integrity scope {ScopeId} is unavailable: {ErrorType}.", selected.Id, ex.GetType().Name);
                                    }
                                }
                            }

                            async Task<IntegrityReadResult> CollectUnderBudget(IntegrityScope selected, CancellationToken token)
                                => await WithBudget(async inner =>
                                {
                                    var result = await (collectOverride ?? IntegrityCollector.ReadAsync)(selected, inner);
                                    // Snapshot comparisons cannot observe transient changes between passes.
                                    return result with { CoverageGap = true };
                                }, token, true);

                            async Task<T> WithBudget<T>(Func<CancellationToken, Task<T>> action, CancellationToken token, bool checkThreshold)
                            {
                                await using var lease = await budget.AcquireAsync(token);
                                var metric = metricSettings.ForOwner(owner);
                                var latest = settings.SavedForOwner(owner);
                                if (metric is null || latest?.SaveToken != saved?.SaveToken ||
                                    configuration["Vigiloom:Agent:StateDirectory"] != directory || configuration["Vigiloom:Agent:Server"] != server)
                                    throw new IntegrityDeferredException();
                                budget.Cpu.SetAllowance(TimeSpan.FromMilliseconds(metric.CpuMillisecondsPerMinute));
                                budget.Io.SetAllowance((ulong)metric.IoLimitMiBPerMinute * 1024 * 1024);
                                if (checkThreshold && (budget.Cpu.Delay() > TimeSpan.Zero || budget.Io.Delay() > TimeSpan.Zero ||
                                    SamplingMemoryBudget.IsExceeded(resources.ReadWorkingSetBytes(), (long)metric.WorkingSetLimitMiB * 1024 * 1024)))
                                    throw new IntegrityDeferredException();
                                var cpu = resources.ReadCpuTime();
                                var io = await resources.ReadIoAsync(token);
                                try { return await action(token); }
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
                    }
                    else if (active is not null)
                    {
                        if (ownership is not null) await ownership.DisposeAsync();
                        ownership = null;
                        active = null;
                        schedule.Reset();
                        marked.Clear();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                    UnauthorizedAccessException or CryptographicException or System.Data.Common.DbException or Win32Exception)
                { logger.LogWarning("Agent integrity collection is unavailable: {ErrorType}.", ex.GetType().Name); }
                try { await Task.Delay(PollInterval, clock, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally { if (ownership is not null) await ownership.DisposeAsync(); }
    }

    private sealed class IntegrityDeferredException : Exception;
}
