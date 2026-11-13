using System.Diagnostics;
using Vigiloom.Agent.Core.Metrics;
using LinuxProcessIo = Vigiloom.Agent.Platforms.Linux.Metrics.ProcessIoReader;
using WindowsProcessIo = Vigiloom.Agent.Platforms.Windows.Metrics.ProcessIoReader;

namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Observes resources of this Agent process, not the monitored host as a whole.</summary>
public interface IMetricResourceMonitor : IDisposable
{
    long ReadWorkingSetBytes();
    TimeSpan ReadCpuTime();
    ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token);
}

internal sealed class MetricResourceMonitor : IMetricResourceMonitor
{
    private readonly Process process = Process.GetCurrentProcess();

    public long ReadWorkingSetBytes() { process.Refresh(); return process.WorkingSet64; }
    public TimeSpan ReadCpuTime() { process.Refresh(); return process.TotalProcessorTime; }

    public async ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) return WindowsProcessIo.Read();
        if (OperatingSystem.IsLinux()) return await LinuxProcessIo.ReadAsync(token) ?? throw new InvalidDataException("Agent I/O counters are unavailable.");
        throw new PlatformNotSupportedException("Agent I/O counters require Windows or Linux.");
    }

    public void Dispose() => process.Dispose();
}
