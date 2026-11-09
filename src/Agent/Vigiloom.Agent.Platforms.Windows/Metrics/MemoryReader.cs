using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Platforms.Windows.Metrics;

/// <summary>Physical memory available to the system; values do not describe pagefile capacity.</summary>
public readonly record struct MemorySnapshot(ulong TotalBytes, ulong AvailableBytes);

[SupportedOSPlatform("windows")]
public static partial class MemoryReader
{
    public static MemorySnapshot Read()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref state) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new(state.TotalPhysical, state.AvailablePhysical);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GlobalMemoryStatusEx(ref MemoryStatus state);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
