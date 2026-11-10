using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Metrics;

namespace Vigiloom.Agent.Platforms.Windows.Metrics;

[SupportedOSPlatform("windows")]
public static partial class ProcessIoReader
{
    public static ProcessIoSnapshot Read()
    {
        if (GetProcessIoCounters(GetCurrentProcess(), out var counters) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new(counters.ReadBytes, counters.WriteBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [LibraryImport("kernel32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetProcessIoCounters(nint process, out IoCounters counters);
}
