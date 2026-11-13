using System.ComponentModel;
using System.Net.NetworkInformation;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Contracts.Agent;
using LinuxCpuReader = Vigiloom.Agent.Platforms.Linux.Metrics.CpuReader;
using LinuxCpuTicks = Vigiloom.Agent.Platforms.Linux.Metrics.CpuTicks;
using LinuxMemoryReader = Vigiloom.Agent.Platforms.Linux.Metrics.MemoryReader;
using LinuxLoadReader = Vigiloom.Agent.Platforms.Linux.Metrics.LoadReader;
using LinuxDiskReader = Vigiloom.Agent.Platforms.Linux.Metrics.DiskReader;
using LinuxDiskCounters = Vigiloom.Agent.Platforms.Linux.Metrics.DiskCounters;
using LinuxVolumeReader = Vigiloom.Agent.Platforms.Linux.Metrics.VolumeReader;
using WindowsMemoryReader = Vigiloom.Agent.Platforms.Windows.Metrics.MemoryReader;
using WindowsPageFileReader = Vigiloom.Agent.Platforms.Windows.Metrics.PageFileReader;
using WindowsCpuReader = Vigiloom.Agent.Platforms.Windows.Metrics.CpuReader;
using WindowsCpuTicks = Vigiloom.Agent.Platforms.Windows.Metrics.CpuTicks;
using WindowsCpuCoreReader = Vigiloom.Agent.Platforms.Windows.Metrics.CpuCoreReader;
using WindowsDiskReader = Vigiloom.Agent.Platforms.Windows.Metrics.DiskReader;

namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>One collection result. Failed or unsupported sources never manufacture zero readings.</summary>
internal sealed record HostMetricCollection(IReadOnlyList<AgentMetricPoint> Points, IReadOnlyList<string> UnavailableSources);

internal interface IHostMetricCollector : IDisposable
{
    Task<HostMetricCollection> CollectAsync(TimeSpan maximumInterval, CancellationToken token, bool includeProcesses = true);
    HostMetricCollection CollectProcesses(TimeSpan maximumInterval, CancellationToken token, TimeSpan? collectionBudget = null);
}

/// <summary>Collects host metrics without sharing counter state across Agent instances.</summary>
internal sealed class HostMetricCollector(TimeProvider clock) : IHostMetricCollector
{
    private readonly NetworkSampler network = new(clock);
    private readonly ProcessSampler processes = new(clock);
    private IReadOnlyDictionary<string, LinuxCpuTicks>? previousCpu;
    private long previousCpuTimestamp;
    private WindowsCpuTicks? previousWindowsCpu;
    private long previousWindowsCpuTimestamp;
    private WindowsCpuCoreReader? windowsCores;
    private WindowsDiskReader? windowsDisks;
    private IReadOnlyDictionary<string, LinuxDiskCounters>? previousDisks;
    private long previousDiskTimestamp;

    public async Task<HostMetricCollection> CollectAsync(TimeSpan maximumInterval, CancellationToken token, bool includeProcesses = true)
    {
        token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        var points = new List<AgentMetricPoint>();
        var unavailable = new List<string>();
        if (OperatingSystem.IsLinux())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var cpus = await LinuxCpuReader.ReadAsync(token);
                var now = clock.GetUtcNow();
                var timestamp = clock.GetTimestamp();
                if (cpus is null) { previousCpu = null; unavailable.Add("cpu"); }
                else
                {
                    if (previousCpu is not null)
                    {
                        var elapsed = clock.GetElapsedTime(previousCpuTimestamp, timestamp);
                        foreach (var (name, current) in cpus)
                        {
                            if (!previousCpu.TryGetValue(name, out var old)) continue;
                            var value = current.PercentSince(old, elapsed, maximumInterval);
                            if (value is { } percent)
                                Add(points, now, name == "cpu" ? AgentMetricKind.CpuTotalPercent : AgentMetricKind.CpuCorePercent,
                                    "linux.proc.stat", name == "cpu" ? null : name[3..], percent);
                        }
                    }
                    previousCpu = cpus;
                    previousCpuTimestamp = timestamp;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                previousCpu = null;
                unavailable.Add("cpu");
            }
            try
            {
                var memory = await LinuxMemoryReader.ReadAsync(token);
                var now = clock.GetUtcNow();
                if (memory is { } result)
                {
                    Add(points, now, AgentMetricKind.MemoryTotalBytes, "linux.proc.meminfo", null, result.TotalBytes);
                    Add(points, now, AgentMetricKind.MemoryAvailableBytes, "linux.proc.meminfo", null, result.AvailableBytes);
                    if (result.SwapTotalBytes is { } total) Add(points, now, AgentMetricKind.SwapTotalBytes, "linux.proc.meminfo", null, total);
                    if (result.SwapFreeBytes is { } free) Add(points, now, AgentMetricKind.SwapFreeBytes, "linux.proc.meminfo", null, free);
                }
                else unavailable.Add("memory");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unavailable.Add("memory"); }
            token.ThrowIfCancellationRequested();
            try
            {
                var load = await LinuxLoadReader.ReadAsync(token);
                var now = clock.GetUtcNow();
                if (load is { } averages)
                {
                    Add(points, now, AgentMetricKind.LoadAverage1, "linux.proc.loadavg", null, averages.OneMinute);
                    Add(points, now, AgentMetricKind.LoadAverage5, "linux.proc.loadavg", null, averages.FiveMinutes);
                    Add(points, now, AgentMetricKind.LoadAverage15, "linux.proc.loadavg", null, averages.FifteenMinutes);
                }
                else unavailable.Add("load");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unavailable.Add("load"); }
            token.ThrowIfCancellationRequested();
            try
            {
                var disks = await LinuxDiskReader.ReadAsync(token);
                var timestamp = clock.GetTimestamp();
                if (disks is null)
                {
                    previousDisks = null;
                    unavailable.Add("disk_io");
                }
                else
                {
                    if (disks.Truncated || previousDisks is null) unavailable.Add("disk_io");
                    if (previousDisks is not null)
                    {
                        var elapsed = clock.GetElapsedTime(previousDiskTimestamp, timestamp);
                        foreach (var (name, current) in disks.Devices)
                        {
                            if (!previousDisks.TryGetValue(name, out var old) || current.Since(old, elapsed, maximumInterval) is not { } rates)
                            {
                                if (!unavailable.Contains("disk_io")) unavailable.Add("disk_io");
                                continue;
                            }
                            var observedAt = clock.GetUtcNow();
                            Add(points, observedAt, AgentMetricKind.DiskReadBytesPerSecond, "linux.proc.diskstats", name, rates.ReadBytesPerSecond);
                            Add(points, observedAt, AgentMetricKind.DiskWriteBytesPerSecond, "linux.proc.diskstats", name, rates.WriteBytesPerSecond);
                            if (rates.ReadLatencyMilliseconds is { } readLatency)
                                Add(points, observedAt, AgentMetricKind.DiskReadLatencyMilliseconds, "linux.proc.diskstats", name, readLatency);
                            if (rates.WriteLatencyMilliseconds is { } writeLatency)
                                Add(points, observedAt, AgentMetricKind.DiskWriteLatencyMilliseconds, "linux.proc.diskstats", name, writeLatency);
                        }
                    }
                    if (disks.Devices.Count == 0 && !unavailable.Contains("disk_io")) unavailable.Add("disk_io");
                    previousDisks = disks.Devices;
                    previousDiskTimestamp = timestamp;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                previousDisks = null;
                unavailable.Add("disk_io");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            try
            {
                windowsCores ??= new WindowsCpuCoreReader(clock);
                var coreSample = windowsCores.Read(maximumInterval);
                var coreTime = clock.GetUtcNow();
                if (coreSample.Incomplete) unavailable.Add("cpu_cores");
                foreach (var (name, value) in coreSample.Cores)
                    Add(points, coreTime, AgentMetricKind.CpuCorePercent, "windows.pdh.processor-information", name, value);
                if (coreSample.Total is { } total)
                    Add(points, coreTime, AgentMetricKind.CpuTotalPercent, "windows.pdh.processor-information", null, total);
            }
            catch (InvalidOperationException)
            {
                if (OperatingSystem.IsWindows()) windowsCores?.Dispose();
                windowsCores = null;
                unavailable.Add("cpu_cores");
            }
            try
            {
                var cpu = WindowsCpuReader.Read();
                var now = clock.GetUtcNow();
                var timestamp = clock.GetTimestamp();
                if (!points.Any(x => x.Kind == AgentMetricKind.CpuTotalPercent) && cpu is { } current && previousWindowsCpu is { } old &&
                    current.PercentSince(old, clock.GetElapsedTime(previousWindowsCpuTimestamp, timestamp), maximumInterval) is { } percent)
                    Add(points, now, AgentMetricKind.CpuTotalPercent, "windows.system-times", null, percent);
                previousWindowsCpu = cpu;
                previousWindowsCpuTimestamp = timestamp;
            }
            catch (Win32Exception) { previousWindowsCpu = null; }
            token.ThrowIfCancellationRequested();
            try
            {
                var memory = WindowsMemoryReader.Read();
                var now = clock.GetUtcNow();
                Add(points, now, AgentMetricKind.MemoryTotalBytes, "windows.memory", null, memory.TotalBytes);
                Add(points, now, AgentMetricKind.MemoryAvailableBytes, "windows.memory", null, memory.AvailableBytes);
            }
            catch (Win32Exception) { unavailable.Add("memory"); }
            try
            {
                var pageFile = WindowsPageFileReader.Read();
                var now = clock.GetUtcNow();
                Add(points, now, AgentMetricKind.PageFileTotalBytes, "windows.pagefile", null, pageFile.TotalBytes);
                Add(points, now, AgentMetricKind.PageFileUsedBytes, "windows.pagefile", null, pageFile.UsedBytes);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException) { unavailable.Add("pagefile"); }
            try
            {
                windowsDisks ??= new WindowsDiskReader(clock);
                var sample = windowsDisks.Read(maximumInterval);
                var now = clock.GetUtcNow();
                if (sample.Incomplete) unavailable.Add("disk_io");
                foreach (var disk in sample.Disks)
                {
                    if (disk.ReadBytesPerSecond is { } read) Add(points, now, AgentMetricKind.DiskReadBytesPerSecond, "windows.pdh.physicaldisk", disk.Instance, read);
                    if (disk.WriteBytesPerSecond is { } write) Add(points, now, AgentMetricKind.DiskWriteBytesPerSecond, "windows.pdh.physicaldisk", disk.Instance, write);
                    if (disk.ReadLatencyMilliseconds is { } readLatency) Add(points, now, AgentMetricKind.DiskReadLatencyMilliseconds, "windows.pdh.physicaldisk", disk.Instance, readLatency);
                    if (disk.WriteLatencyMilliseconds is { } writeLatency) Add(points, now, AgentMetricKind.DiskWriteLatencyMilliseconds, "windows.pdh.physicaldisk", disk.Instance, writeLatency);
                }
            }
            catch (InvalidOperationException)
            {
                windowsDisks?.Dispose();
                windowsDisks = null;
                unavailable.Add("disk_io");
            }
        }
        else { unavailable.Add("cpu"); unavailable.Add("memory"); }

        if (!points.Any(x => x.Kind == AgentMetricKind.CpuTotalPercent) && !unavailable.Contains("cpu")) unavailable.Add("cpu");

        token.ThrowIfCancellationRequested();
        try
        {
            var snapshot = network.Sample(maximumInterval);
            var now = clock.GetUtcNow();
            if (snapshot.Truncated || snapshot.UnavailableInterfaces > 0) unavailable.Add("network");
            foreach (var item in snapshot.Interfaces)
            {
                if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 256 || item.Id.Any(char.IsControl))
                {
                    if (!unavailable.Contains("network")) unavailable.Add("network");
                    continue;
                }
                AddOptional(points, now, AgentMetricKind.NetworkReceivedBytesPerSecond, item.Id, item.ReceivedBytesPerSecond);
                AddOptional(points, now, AgentMetricKind.NetworkSentBytesPerSecond, item.Id, item.SentBytesPerSecond);
                AddOptional(points, now, AgentMetricKind.NetworkReceivedErrorsPerSecond, item.Id, item.ReceivedErrorsPerSecond);
                AddOptional(points, now, AgentMetricKind.NetworkSentErrorsPerSecond, item.Id, item.SentErrorsPerSecond);
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            network.Reset();
            unavailable.Add("network");
        }
        if (!points.Any(x => x.Kind is AgentMetricKind.NetworkReceivedBytesPerSecond or AgentMetricKind.NetworkSentBytesPerSecond) &&
            !unavailable.Contains("network")) unavailable.Add("network");
        token.ThrowIfCancellationRequested();
        try
        {
            var volumes = OperatingSystem.IsLinux() ? await LinuxVolumeReader.ReadAsync(token) : VolumeReader.ReadWindows();
            var now = clock.GetUtcNow();
            if (volumes.Truncated || volumes.UnavailableVolumes > 0 || volumes.Volumes.Count == 0) unavailable.Add("volume");
            foreach (var volume in volumes.Volumes)
            {
                Add(points, now, AgentMetricKind.VolumeTotalBytes, "system.volume", volume.Mount, volume.TotalBytes);
                Add(points, now, AgentMetricKind.VolumeAvailableBytes, "system.volume", volume.Mount, volume.AvailableBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            unavailable.Add("volume");
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) unavailable.Add("disk_io");
        if (includeProcesses)
            CollectProcesses(points, unavailable, maximumInterval, token);
        token.ThrowIfCancellationRequested();
        return new(points, unavailable);
    }

    public HostMetricCollection CollectProcesses(TimeSpan maximumInterval, CancellationToken token, TimeSpan? collectionBudget = null)
    {
        token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        var points = new List<AgentMetricPoint>();
        var unavailable = new List<string>();
        CollectProcesses(points, unavailable, maximumInterval, token, collectionBudget);
        return new(points, unavailable);
    }

    private void CollectProcesses(List<AgentMetricPoint> points, List<string> unavailable, TimeSpan maximumInterval, CancellationToken token, TimeSpan? collectionBudget = null)
    {
        try
        {
            var sample = processes.Sample(maximumInterval, token, collectionBudget);
            if (sample.BudgetExhausted) unavailable.Add("process_budget");
            if (sample.Truncated || sample.UnavailableProcesses > 0 || sample.Processes.Count == 0 && !sample.BudgetExhausted) unavailable.Add("process");
            var observedAt = clock.GetUtcNow();
            foreach (var item in sample.Processes)
            {
                var dimension = $"{item.Counters.Id}:{item.Counters.StartedUtcTicks}";
                Add(points, observedAt, AgentMetricKind.ProcessWorkingSetBytes, "system.process", dimension, item.Counters.WorkingSetBytes);
                if (item.CpuPercent is { } percent)
                    Add(points, observedAt, AgentMetricKind.ProcessCpuPercent, "system.process", dimension, percent);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            processes.Reset();
            unavailable.Add("process");
        }
    }

    private static void AddOptional(List<AgentMetricPoint> points, DateTimeOffset at, AgentMetricKind kind, string dimension, double? value)
    {
        if (value is { } number) Add(points, at, kind, "system.network", dimension, number);
    }

    private static void Add(List<AgentMetricPoint> points, DateTimeOffset at, AgentMetricKind kind, string source, string? dimension, double value) =>
        points.Add(new(Guid.CreateVersion7(), at, kind, source, dimension, value));

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) windowsCores?.Dispose();
        if (OperatingSystem.IsWindows()) windowsDisks?.Dispose();
    }
}
