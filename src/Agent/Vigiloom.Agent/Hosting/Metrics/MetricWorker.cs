using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;

namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Runs one owner's metric collection serially and retries pending batches.</summary>
internal sealed class MetricWorker(AgentMetricSettingsRuntime settings, IConfiguration configuration, TimeProvider clock,
    ILogger<MetricWorker> logger, IMetricResourceMonitor? resourceMonitor = null, SamplingBudgetCoordinator? sharedBudget = null) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentSettingsOwner? activeOwner = null;
        MetricSamplingCycle? cycle = null;
        MetricLossLedger? ledger = null;
        IdentityVault.MetricOwnership? producerOwnership = null;
        string? lastStatus = null;
        var cpuThrottled = false;
        var memoryThrottled = false;
        var ioThrottled = false;
        var loggedUnavailable = new HashSet<string>(StringComparer.Ordinal);
        using var resources = resourceMonitor ?? new MetricResourceMonitor();
        var budget = sharedBudget ?? new SamplingBudgetCoordinator(clock);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = CheckInterval;
                try
                {
                    var directory = configuration["Vigiloom:Agent:StateDirectory"];
                    var server = configuration["Vigiloom:Agent:Server"];
                    if (string.IsNullOrWhiteSpace(directory) || !Uri.TryCreate(server, UriKind.Absolute, out var uri))
                    {
                        if (cycle?.HasPending == true) logger.LogWarning("Agent configuration changed with {Batches} metric batches and {Records} records not queued.", cycle.PendingBatches, cycle.PendingRecords);
                        if (activeOwner is not null) settings.ClearLossWriter(activeOwner);
                        if (cycle is not null && producerOwnership is not null) await CloseCycleAsync(cycle, ledger, producerOwnership);
                        cycle = null;
                        ledger = null;
                        producerOwnership = null;
                        activeOwner = null;
                        settings.Bind(null);
                    }
                    else
                    {
                        var vault = new IdentityVault(directory);
                        var owner = await AgentSettingsClient.GetOwnerAsync(vault, uri, stoppingToken);
                        if (!IsCurrent(directory, server)) continue;
                        if (owner != activeOwner)
                        {
                            if (cycle?.HasPending == true) logger.LogWarning("Agent identity changed with {Batches} metric batches and {Records} records not queued.", cycle.PendingBatches, cycle.PendingRecords);
                            if (activeOwner is not null) settings.ClearLossWriter(activeOwner);
                            if (cycle is not null && producerOwnership is not null) await CloseCycleAsync(cycle, ledger, producerOwnership);
                            cycle = null;
                            ledger = null;
                            producerOwnership = null;
                            activeOwner = null;
                            if (owner is not null)
                            {
                                producerOwnership = vault.AcquireMetricOwnership();
                                try { cycle = new MetricSamplingCycle(owner, new DurableBatchQueue(vault, owner.AgentId), clock); }
                                catch { await producerOwnership.DisposeAsync(); producerOwnership = null; throw; }
                            }
                            activeOwner = owner;
                            loggedUnavailable.Clear();
                            settings.Bind(owner);
                        }
                        if (owner is not null && cycle is not null && settings.ForOwner(owner) is { } policy)
                        {
                            if (ledger is not null)
                            {
                                try { await ledger.ReadAsync(stoppingToken); }
                                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                                    UnauthorizedAccessException or CryptographicException or System.Data.Common.DbException)
                                {
                                    settings.ClearLossWriter(owner);
                                    await ledger.DisposeAsync();
                                    ledger = null;
                                    logger.LogWarning("Agent metric loss accounting unavailable; collection continues: {ErrorType}.", ex.GetType().Name);
                                }
                            }
                            if (ledger is null && producerOwnership is not null)
                            {
                                ledger = await TryStartLedgerAsync(vault, owner, producerOwnership, stoppingToken);
                                if (ledger is not null) settings.PublishLossWriter(owner, ledger.WriterId);
                            }
                            // Pending observations can become durable even when resource
                            // probes fail or a budget prevents starting new collection.
                            var pending = cycle.FlushPendingAsync(stoppingToken);
                            var pendingStatus = pending is null ? (EnqueueResult?)null : await pending;
                            MetricCycleResult result;
                            if (pendingStatus is not (null or EnqueueResult.Queued))
                                result = new(pendingStatus, CheckInterval, []);
                            else
                            {
                                await using var budgetLease = await budget.AcquireAsync(stoppingToken);
                                var cpuBudget = budget.Cpu;
                                var ioBudget = budget.Io;
                                var latestPolicy = settings.ForOwner(owner);
                                if (latestPolicy is null || !IsCurrent(directory, server))
                                {
                                    result = new(pendingStatus, CheckInterval, []);
                                }
                                else
                                {
                                    policy = latestPolicy;
                                    cpuBudget.SetAllowance(TimeSpan.FromMilliseconds(policy.CpuMillisecondsPerMinute));
                                    ioBudget.SetAllowance((ulong)policy.IoLimitMiBPerMinute * 1024 * 1024);
                                    var throttle = cpuBudget.Delay();
                                    var ioDelay = ioBudget.Delay();
                                    if ((ioDelay > TimeSpan.Zero) != ioThrottled)
                                    {
                                        ioThrottled = ioDelay > TimeSpan.Zero;
                                        if (ioThrottled) logger.LogWarning("Agent metric collection is delayed by the I/O budget.");
                                        else logger.LogInformation("Agent metric collection resumed after the I/O budget delay.");
                                    }
                                    var memoryExceeded = SamplingMemoryBudget.IsExceeded(resources.ReadWorkingSetBytes(), (long)policy.WorkingSetLimitMiB * 1024 * 1024);
                                    if (memoryExceeded != memoryThrottled)
                                    {
                                        memoryThrottled = memoryExceeded;
                                        if (memoryThrottled) logger.LogWarning("Agent metric collection is delayed by the memory budget.");
                                        else logger.LogInformation("Agent metric collection resumed after the memory budget delay.");
                                    }
                                    if ((throttle > TimeSpan.Zero) != cpuThrottled)
                                    {
                                        cpuThrottled = throttle > TimeSpan.Zero;
                                        if (cpuThrottled) logger.LogWarning("Agent metric collection is delayed by the CPU budget.");
                                        else logger.LogInformation("Agent metric collection resumed after the CPU budget delay.");
                                    }
                                    if (throttle > TimeSpan.Zero || ioDelay > TimeSpan.Zero || memoryExceeded)
                                    {
                                        var wait = throttle > ioDelay ? throttle : ioDelay;
                                        result = new(pendingStatus, memoryExceeded ? CheckInterval : wait, []);
                                    }
                                    else
                                    {
                                        var before = resources.ReadCpuTime();
                                        var ioBefore = await resources.ReadIoAsync(stoppingToken);
                                        try { result = await cycle.RunAsync(policy, stoppingToken); }
                                        finally
                                        {
                                            try { ioBudget.Record(ioBefore, await resources.ReadIoAsync(CancellationToken.None)); }
                                            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException)
                                            {
                                                ioBudget.MarkUnknown();
                                                logger.LogWarning("Agent I/O budget observation failed: {ErrorType}.", ex.GetType().Name);
                                            }
                                            finally
                                            {
                                                try { cpuBudget.Record(before, resources.ReadCpuTime()); }
                                                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
                                                {
                                                    cpuBudget.MarkUnknown();
                                                    logger.LogWarning("Agent CPU budget observation failed: {ErrorType}.", ex.GetType().Name);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            if (result.Status == EnqueueResult.OwnerChanged)
                            {
                                logger.LogWarning("Metric collection stopped because the Agent identity changed; {Batches} batches and {Records} records were not queued.", cycle.PendingBatches, cycle.PendingRecords);
                                settings.ClearLossWriter(owner);
                                if (producerOwnership is not null) await CloseCycleAsync(cycle, ledger, producerOwnership);
                                cycle = null;
                                ledger = null;
                                producerOwnership = null;
                                activeOwner = null;
                                settings.Bind(null);
                            }
                            else
                            {
                                var status = result.Status?.ToString() ?? "Waiting";
                                if (!string.Equals(lastStatus, status, StringComparison.Ordinal) && result.Status is not null)
                                {
                                    if (result.Status == EnqueueResult.Queued) logger.LogInformation("Agent metric queue: {Status}.", status);
                                    else logger.LogWarning("Agent metric queue: {Status}.", status);
                                    lastStatus = status;
                                }
                                foreach (var source in result.UnavailableSources)
                                    if (loggedUnavailable.Add(source)) logger.LogWarning("Agent metric source unavailable: {Source}.", source);
                                if (result.Delay > TimeSpan.Zero && result.Delay < delay) delay = result.Delay;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                    UnauthorizedAccessException or JsonException or CryptographicException or System.Data.Common.DbException or Win32Exception or PlatformNotSupportedException)
                {
                    logger.LogWarning("Agent metric sampling failed: {ErrorType}.", ex.GetType().Name);
                }
                try { await Task.Delay(delay, clock, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            try
            {
                if (cycle?.HasPending == true)
                {
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var result = await cycle.FlushForShutdownAsync(deadline.Token);
                        if (result != EnqueueResult.Queued)
                            logger.LogWarning("Agent shutdown left {Batches} metric batches and {Records} records not queued: {Status}.", cycle.PendingBatches, cycle.PendingRecords, result);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                        UnauthorizedAccessException or JsonException or CryptographicException or System.Data.Common.DbException)
                    {
                        logger.LogWarning("Agent shutdown left {Batches} metric batches and {Records} records not queued: {ErrorType}.", cycle.PendingBatches, cycle.PendingRecords, ex.GetType().Name);
                    }
                }
            }
            finally
            {
                if (activeOwner is not null) settings.ClearLossWriter(activeOwner);
                if (cycle is not null && producerOwnership is not null) await CloseCycleAsync(cycle, ledger, producerOwnership);
            }
        }
    }

    private async Task<MetricLossLedger?> TryStartLedgerAsync(IdentityVault vault, AgentSettingsOwner owner,
        IdentityVault.MetricOwnership ownership, CancellationToken token)
    {
        try { return await MetricLossLedger.StartUnderOwnershipAsync(vault, owner, ownership, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
            UnauthorizedAccessException or CryptographicException or System.Data.Common.DbException)
        {
            logger.LogWarning("Agent metric loss accounting unavailable; collection continues: {ErrorType}.", ex.GetType().Name);
            return null;
        }
    }

    private async Task CloseCycleAsync(MetricSamplingCycle cycle, MetricLossLedger? ledger, IdentityVault.MetricOwnership ownership)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (ledger is null) return;
            if (cycle.HasPending)
            {
                if (cycle.KnownUnqueuedBatches > 0)
                    await ledger.RecordAsync(1, cycle.KnownUnqueuedBatches, cycle.KnownUnqueuedRecords, deadline.Token);
                if (cycle.PendingOutcomeUnknown) await ledger.MarkIncompleteAsync(deadline.Token);
            }
            await ledger.CompleteAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or InvalidOperationException or
            ArgumentException or UnauthorizedAccessException or CryptographicException or System.Data.Common.DbException)
        {
            // An unfinished writer session causes the next owner to report
            // unverified completeness, never a fabricated successful count.
            logger.LogWarning("Agent metric loss accounting could not be completed: {ErrorType}.", ex.GetType().Name);
        }
        finally
        {
            try { cycle.Dispose(); }
            finally
            {
                try { if (ledger is not null) await ledger.DisposeAsync(); }
                finally { await ownership.DisposeAsync(); }
            }
        }
    }

    private bool IsCurrent(string directory, string server) =>
        string.Equals(directory, configuration["Vigiloom:Agent:StateDirectory"], StringComparison.Ordinal) &&
        string.Equals(server, configuration["Vigiloom:Agent:Server"], StringComparison.Ordinal);

}
