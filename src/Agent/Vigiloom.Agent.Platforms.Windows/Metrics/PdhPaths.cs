using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Platforms.Windows.Metrics;

/// <summary>Resolves an English PDH wildcard to localized, expanded counter paths.</summary>
[SupportedOSPlatform("windows")]
internal static partial class PdhPaths
{
    private const uint MoreData = 0x800007D2;
    private const uint MaxBytes = 1024 * 1024;
    private const uint RefreshCounters = 4;

    public static string[] Expand(string englishPath)
    {
        if (OpenQuery(null, 0, out var raw) != 0) throw new InvalidOperationException("Unable to open a PDH query.");
        using var query = new QueryHandle(raw);
        if (AddEnglish(query, englishPath, 0, out var counter) != 0)
            throw new InvalidOperationException("Unable to locate the Windows disk counter.");
        var localized = GetLocalizedPath(counter);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint length = 0;
            var status = ExpandWildcard(null, localized, null, ref length, RefreshCounters);
            if (status != MoreData || length is 0 or > MaxBytes / 2) throw new InvalidOperationException("Unable to enumerate Windows disk counters.");
            var buffer = new char[length];
            status = ExpandWildcard(null, localized, buffer, ref length, 0);
            if (status == MoreData) continue;
            if (status != 0 || length > buffer.Length) throw new InvalidOperationException("Unable to enumerate Windows disk counters.");
            return new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        throw new InvalidOperationException("Windows disk counters changed during enumeration.");
    }

    private static string GetLocalizedPath(nint counter)
    {
        uint length = 0;
        if (GetInfo(counter, 0, ref length, 0) != MoreData || length is 0 or > MaxBytes)
            throw new InvalidOperationException("Unable to localize the Windows disk counter.");
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (GetInfo(counter, 0, ref length, buffer) != 0) throw new InvalidOperationException("Unable to localize the Windows disk counter.");
            // PDH_COUNTER_INFO starts with six 32-bit fields followed by two DWORD_PTR fields.
            var pathPointer = Marshal.ReadIntPtr(buffer, 24 + 2 * IntPtr.Size);
            return Marshal.PtrToStringUni(pathPointer) ?? throw new InvalidOperationException("The localized disk counter path is missing.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle(nint handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseQuery(handle) == 0;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint OpenQuery(string? source, nuint data, out nint query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint AddEnglish(QueryHandle query, string path, nuint data, out nint counter);
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetCounterInfoW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint GetInfo(nint counter, byte explain, ref uint length, nint buffer);
    [LibraryImport("pdh.dll", EntryPoint = "PdhExpandWildCardPathW", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint ExpandWildcard(string? source, string path, [Out] char[]? buffer, ref uint length, uint flags);
    [LibraryImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint CloseQuery(nint query);
}
