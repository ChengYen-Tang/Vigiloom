using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Platforms.Windows.Metrics;

/// <summary>Aggregate installed pagefile capacity and usage, not system commit limit.</summary>
public readonly record struct PageFileSnapshot(ulong TotalBytes, ulong UsedBytes);

[SupportedOSPlatform("windows")]
public static unsafe partial class PageFileReader
{
    private const int MaximumPageFiles = 256;

    public static PageFileSnapshot Read()
    {
        var state = new Accumulator();
        if (EnumPageFiles(&Visit, &state) == 0)
        {
            if (state.Invalid != 0) throw new InvalidDataException("Pagefile data exceeded supported bounds.");
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        if (state.Invalid != 0 || state.UsedPages > state.TotalPages ||
            state.TotalPages > ulong.MaxValue / (ulong)Environment.SystemPageSize ||
            state.UsedPages > ulong.MaxValue / (ulong)Environment.SystemPageSize)
            throw new InvalidDataException("Invalid pagefile counters.");
        return new(state.TotalPages * (ulong)Environment.SystemPageSize, state.UsedPages * (ulong)Environment.SystemPageSize);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Visit(Accumulator* state, PageFileInfo* info, char* filename)
    {
        if (state == null || info == null) return 0;
        var total = (ulong)info->TotalSize;
        var used = (ulong)info->TotalInUse;
        if (++state->Count > MaximumPageFiles || used > total ||
            total > ulong.MaxValue - state->TotalPages || used > ulong.MaxValue - state->UsedPages)
        {
            state->Invalid = 1;
            return 0;
        }
        state->TotalPages += total;
        state->UsedPages += used;
        return 1;
    }

    [LibraryImport("psapi.dll", EntryPoint = "EnumPageFilesW", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int EnumPageFiles(delegate* unmanaged[Stdcall]<Accumulator*, PageFileInfo*, char*, int> callback, Accumulator* state);

    [StructLayout(LayoutKind.Sequential)]
    private struct PageFileInfo
    {
        public uint Size;
        public uint Reserved;
        public nuint TotalSize;
        public nuint TotalInUse;
        public nuint PeakUsage;
    }

    private struct Accumulator
    {
        public ulong TotalPages;
        public ulong UsedPages;
        public int Count;
        public int Invalid;
    }
}
