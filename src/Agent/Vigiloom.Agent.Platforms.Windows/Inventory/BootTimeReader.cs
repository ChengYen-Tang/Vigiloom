using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Platforms.Windows.Inventory;

/// <summary>Reads the Windows-reported operating-system boot time.</summary>
[SupportedOSPlatform("windows")]
public static class BootTimeReader
{
    public static DateTimeOffset? Read(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(30) };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT LastBootUpTime FROM Win32_OperatingSystem"), options);
            using var results = searcher.Get();
            using var iterator = results.GetEnumerator();
            if (!iterator.MoveNext()) return null;
            using var item = (ManagementObject)iterator.Current;
            token.ThrowIfCancellationRequested();
            if (item["LastBootUpTime"] is not string value) return null;
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(value)).ToUniversalTime();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or ArgumentException or FormatException)
        { return null; }
    }
}
