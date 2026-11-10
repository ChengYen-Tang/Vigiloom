using System.ComponentModel;
using System.Security.Cryptography;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Agent.Platforms.Linux.Logs;
using Vigiloom.Agent.Platforms.Windows.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Logs;

/// <summary>Collects enabled host sources while holding the state directory's exclusive log producer lease.</summary>
internal sealed class LogWorker(AgentLogSettingsRuntime settings, AgentMetricSettingsRuntime metrics, IConfiguration configuration,
    TimeProvider clock, ILogger<LogWorker> logger, IMetricResourceMonitor? monitor = null, SamplingBudgetCoordinator? sharedBudget = null,
    AgentLogStatusRuntime? statusRuntime = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentSettingsOwner? activeOwner = null;
        IAsyncDisposable? ownership = null;
        LogCollectionCycle? cycle = null;
        var last = new Dictionary<Guid, LogSourceCycleStatus>();
        var budget = sharedBudget ?? new SamplingBudgetCoordinator(clock);
        var wasThrottled = false;
        using var resources = monitor ?? new MetricResourceMonitor();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = PollInterval;
                try
                {
                    var directory = configuration["Vigiloom:Agent:StateDirectory"];
                    var server = configuration["Vigiloom:Agent:Server"];
                    if (string.IsNullOrWhiteSpace(directory) || !Uri.TryCreate(server, UriKind.Absolute, out var uri))
                    {
                        if (ownership is not null) await ownership.DisposeAsync();
                        ownership = null; cycle = null; activeOwner = null; last.Clear(); settings.Bind(null); statusRuntime?.Bind(null);
                    }
                    else
                    {
                        var vault = new IdentityVault(directory);
                        var owner = await AgentSettingsClient.GetOwnerAsync(vault, uri, stoppingToken);
                        if (!IsCurrent(directory, server)) continue;
                        if (owner != activeOwner)
                        {
                            if (ownership is not null) await ownership.DisposeAsync();
                            ownership = null; cycle = null; activeOwner = null; last.Clear();
                            if (owner is not null)
                            {
                                ownership = vault.AcquireLogOwnership();
                                try { cycle = new LogCollectionCycle(owner, new DurableBatchQueue(vault, owner.AgentId), clock, CreateReader); }
                                catch { await ownership.DisposeAsync(); ownership = null; throw; }
                            }
                            activeOwner = owner;
                            settings.Bind(owner);
                            statusRuntime?.Bind(owner);
                        }
                        if (owner is not null && cycle is not null && settings.SavedForOwner(owner) is { } saved && metrics.ForOwner(owner) is { } policy)
                        {
                            await using var budgetLease = await budget.AcquireAsync(stoppingToken);
                            var latestPolicy = metrics.ForOwner(owner);
                            var latestSaved = settings.SavedForOwner(owner);
                            if (latestPolicy is null || latestSaved?.SaveToken != saved.SaveToken || !IsCurrent(directory, server)) continue;
                            policy = latestPolicy;
                            var cpu = budget.Cpu;
                            var io = budget.Io;
                            cpu.SetAllowance(TimeSpan.FromMilliseconds(policy.CpuMillisecondsPerMinute));
                            io.SetAllowance((ulong)policy.IoLimitMiBPerMinute * 1024 * 1024);
                            var throttled = cpu.Delay() != TimeSpan.Zero || io.Delay() != TimeSpan.Zero ||
                                SamplingMemoryBudget.IsExceeded(resources.ReadWorkingSetBytes(), (long)policy.WorkingSetLimitMiB * 1024 * 1024);
                            if (throttled != wasThrottled)
                            {
                                wasThrottled = throttled;
                                if (throttled) logger.LogWarning("Agent log collection is delayed by the shared resource budget.");
                                else logger.LogInformation("Agent log collection resumed after the shared resource budget delay.");
                            }
                            if (throttled && settings.SavedForOwner(owner)?.SaveToken == saved.SaveToken)
                                statusRuntime?.Record(owner, saved.Snapshot, true, [], clock.GetUtcNow());
                            if (!throttled)
                            {
                                var beforeCpu = resources.ReadCpuTime();
                                var beforeIo = await resources.ReadIoAsync(stoppingToken);
                                try
                                {
                                    var results = await cycle.RunAsync(saved.Snapshot, saved.SaveToken,
                                        () => IsCurrent(directory, server) && settings.SavedForOwner(owner)?.SaveToken == saved.SaveToken, stoppingToken);
                                    if (IsCurrent(directory, server) && settings.SavedForOwner(owner)?.SaveToken == saved.SaveToken)
                                        statusRuntime?.Record(owner, saved.Snapshot, false, results, clock.GetUtcNow());
                                    if (results.Any(result => result.MayHaveMore) && !results.Any(result => result.Status is LogSourceCycleStatus.Full or LogSourceCycleStatus.SettingsChanged))
                                        delay = TimeSpan.FromMilliseconds(250);
                                    var activeIds = saved.Snapshot.Sources.Where(source => source.Enabled).Select(source => source.SourceId).ToHashSet();
                                    foreach (var removed in last.Keys.Where(id => !activeIds.Contains(id)).ToArray()) last.Remove(removed);
                                    foreach (var result in results)
                                    {
                                        if (last.TryGetValue(result.SourceId, out var prior) && prior == result.Status) continue;
                                        last[result.SourceId] = result.Status;
                                        if (result.Status is LogSourceCycleStatus.Unsupported or LogSourceCycleStatus.Unavailable or LogSourceCycleStatus.Full or LogSourceCycleStatus.Conflict)
                                            logger.LogWarning("Agent log source {SourceId}: {Status}.", result.SourceId, result.Status);
                                    }
                                }
                                finally
                                {
                                    try { io.Record(beforeIo, await resources.ReadIoAsync(CancellationToken.None)); }
                                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException)
                                    { io.MarkUnknown(); logger.LogWarning("Agent log I/O budget observation failed: {ErrorType}.", ex.GetType().Name); }
                                    try { cpu.Record(beforeCpu, resources.ReadCpuTime()); }
                                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
                                    { cpu.MarkUnknown(); logger.LogWarning("Agent log CPU budget observation failed: {ErrorType}.", ex.GetType().Name); }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                    UnauthorizedAccessException or System.Text.Json.JsonException or CryptographicException or System.Data.Common.DbException or Win32Exception or PlatformNotSupportedException)
                { logger.LogWarning("Agent log collection failed: {ErrorType}.", ex.GetType().Name); }
                try { await Task.Delay(delay, clock, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally { if (ownership is not null) await ownership.DisposeAsync(); }
    }

    private ILogSourceReader? CreateReader(AgentLogSourceSetting source) => source.Kind switch
    {
        AgentLogSourceKind.TextFile when source.TextMode is { } mode => new TextFileLogReader(source.Target, mode, clock),
        AgentLogSourceKind.Journal when OperatingSystem.IsLinux() => new JournalLogReader(),
        AgentLogSourceKind.WindowsEvent when OperatingSystem.IsWindows() => new WindowsEventLogReader(source.Target),
        _ => null
    };

    private bool IsCurrent(string directory, string server) =>
        string.Equals(directory, configuration["Vigiloom:Agent:StateDirectory"], StringComparison.Ordinal) &&
        string.Equals(server, configuration["Vigiloom:Agent:Server"], StringComparison.Ordinal);
}
