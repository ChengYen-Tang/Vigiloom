using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Platforms.Windows.Metrics;

/// <summary>Windows CPU counters; kernel time includes idle time.</summary>
public readonly record struct CpuTicks(ulong Idle, ulong Kernel, ulong User)
{
    public double? PercentSince(CpuTicks previous, TimeSpan elapsed, TimeSpan maximumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        if (elapsed <= TimeSpan.Zero || elapsed > maximumInterval || Idle < previous.Idle ||
            Kernel < previous.Kernel || User < previous.User) return null;
        var idle = Idle - previous.Idle;
        var kernel = Kernel - previous.Kernel;
        var user = User - previous.User;
        if (idle > kernel) return null;
        double total = (double)kernel + user;
        return total > 0 ? ((double)(kernel - idle) + user) * 100 / total : null;
    }
}

[SupportedOSPlatform("windows")]
public static partial class CpuReader
{
    /// <summary>Returns no aggregate if GetSystemTimes would cover only one of several processor groups.</summary>
    public static CpuTicks? Read()
    {
        if (GetActiveProcessorGroupCount() != 1) return null;
        if (GetSystemTimes(out var idle, out var kernel, out var user) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new(idle.Value, kernel.Value, user.Value);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [LibraryImport("kernel32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial ushort GetActiveProcessorGroupCount();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }
}
