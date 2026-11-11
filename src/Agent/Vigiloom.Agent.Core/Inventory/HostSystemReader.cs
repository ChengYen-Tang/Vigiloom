using System.Runtime.InteropServices;

namespace Vigiloom.Agent.Core.Inventory;

/// <summary>Basic runtime-visible system details. Processor count is the runtime-start allowance, not physical cores.</summary>
public sealed record HostSystemSnapshot(DateTimeOffset ObservedAt, string HostName, string OperatingSystem,
    string KernelOrPlatformVersion, string OsArchitecture, string ProcessArchitecture, string RuntimeVersion,
    int AvailableProcessorCount);

public static class HostSystemReader
{
    public static HostSystemSnapshot Read(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new(clock.GetUtcNow(), Environment.MachineName, RuntimeInformation.OSDescription,
            Environment.OSVersion.VersionString, RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount);
    }
}
