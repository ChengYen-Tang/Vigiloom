using System.ComponentModel;
using System.Security.Cryptography;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Hosting.Inventory;

/// <summary>Serializes inventory sources and resumes their durable generation before collecting another.</summary>
internal sealed class InventoryWorker(InventoryStatusClient statusClient, AgentMetricSettingsRuntime settings, InventorySettingsRuntime inventorySettings,
    SamplingBudgetCoordinator budget, IConfiguration configuration, TimeProvider clock, ILogger<InventoryWorker> logger,
    IMetricResourceMonitor? resourceMonitor = null,
    Func<InventorySource, CancellationToken, Task<InventoryReadResult<InventoryEntry>>>? collectOverride = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sources = Sources();
        var index = 0;
        var schedule = new InventoryPassSchedule(clock);
        AgentSettingsOwner? active = null;
        IAsyncDisposable? ownership = null;
        using var resources = resourceMonitor ?? new MetricResourceMonitor();
        var collector = new InventoryCollector(clock);
        var cycle = new InventoryCycle(statusClient, clock);
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
                            ownership = owner is null ? null : vault.AcquireInventoryOwnership();
                            active = owner;
                            index = 0;
                            schedule.Reset();
                        }
                        var inventoryPolicy = owner is null ? null : inventorySettings.ApplyForOwner(owner, clock.GetUtcNow());
                        if (owner is not null && inventoryPolicy is not null &&
                            schedule.IsDue(inventoryPolicy.ScanIntervalSeconds))
                        {
                            schedule.Begin();
                            var result = await cycle.RunAsync(vault, uri, sources[index], CollectAsync, stoppingToken, owner, ContinueUnderBudget);
                            if (result.Kind == InventoryCycleKind.QueuedFailure)
                                logger.LogWarning("Agent inventory source {Source} exceeded a collection or queue limit; a failed read was queued.", result.Source);
                            if (result.Kind is InventoryCycleKind.Complete or InventoryCycleKind.Rejected or InventoryCycleKind.TooLarge)
                            {
                                if (result.Kind is InventoryCycleKind.Rejected or InventoryCycleKind.TooLarge)
                                    logger.LogWarning("Agent inventory source {Source} was rejected; it will be retried on the next pass.", result.Source);
                                var completedIndex = Array.IndexOf(sources, result.Source);
                                index = completedIndex < 0 ? 0 : completedIndex + 1;
                                if (index >= sources.Length) { index = 0; schedule.Complete(); }
                            }

                            async Task<InventoryReadResult<InventoryEntry>> CollectAsync(InventorySource source, CancellationToken token)
                                => await WithBudget(inner => collectOverride is null ? collector.ReadAsync(source, inner) : collectOverride(source, inner), token, true);

                            async Task<Vigiloom.Agent.Core.Transport.EnqueueResult> ContinueUnderBudget(
                                Func<CancellationToken, Task<Vigiloom.Agent.Core.Transport.EnqueueResult>> action, CancellationToken token)
                                => await WithBudget(action, token, false);

                            async Task<T> WithBudget<T>(Func<CancellationToken, Task<T>> action, CancellationToken token, bool checkThreshold)
                            {
                                await using var lease = await budget.AcquireAsync(token);
                                var policy = settings.ForOwner(owner);
                                if (policy is null || configuration["Vigiloom:Agent:StateDirectory"] != directory ||
                                    configuration["Vigiloom:Agent:Server"] != server) throw new InventoryDeferredException();
                                budget.Cpu.SetAllowance(TimeSpan.FromMilliseconds(policy.CpuMillisecondsPerMinute));
                                budget.Io.SetAllowance((ulong)policy.IoLimitMiBPerMinute * 1024 * 1024);
                                if (checkThreshold && (budget.Cpu.Delay() > TimeSpan.Zero || budget.Io.Delay() > TimeSpan.Zero ||
                                    SamplingMemoryBudget.IsExceeded(resources.ReadWorkingSetBytes(), (long)policy.WorkingSetLimitMiB * 1024 * 1024)))
                                    throw new InventoryDeferredException();
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
                        index = 0;
                        schedule.Reset();
                    }
                }
                catch (InventoryDeferredException) { }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                    UnauthorizedAccessException or CryptographicException or System.Data.Common.DbException or Win32Exception)
                { logger.LogWarning("Agent inventory collection is unavailable: {ErrorType}.", ex.GetType().Name); }
                try { await Task.Delay(PollInterval, clock, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally { if (ownership is not null) await ownership.DisposeAsync(); }
    }

    private static InventorySource[] Sources() => OperatingSystem.IsWindows()
        ? Environment.Is64BitOperatingSystem
            ? [InventorySource.HostSystem, InventorySource.HostNetwork, InventorySource.HostVolumes, InventorySource.WindowsSoftware32, InventorySource.WindowsSoftware64,
                InventorySource.WindowsAccounts, InventorySource.WindowsGroups, InventorySource.WindowsQuickFix]
            : [InventorySource.HostSystem, InventorySource.HostNetwork, InventorySource.HostVolumes, InventorySource.WindowsSoftware32, InventorySource.WindowsAccounts,
                InventorySource.WindowsGroups, InventorySource.WindowsQuickFix]
        : OperatingSystem.IsLinux()
            ? [InventorySource.HostSystem, InventorySource.HostNetwork, InventorySource.HostVolumes, InventorySource.LinuxDpkg, InventorySource.LinuxRpm,
                InventorySource.LinuxAccounts, InventorySource.LinuxGroups]
            : [InventorySource.HostSystem];

    private sealed class InventoryDeferredException : Exception;
}
