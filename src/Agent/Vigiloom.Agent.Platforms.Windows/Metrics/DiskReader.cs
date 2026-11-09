using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Platforms.Windows.Metrics;

public sealed record DiskReading(string Instance, double? ReadBytesPerSecond, double? WriteBytesPerSecond,
    double? ReadLatencyMilliseconds, double? WriteLatencyMilliseconds);
public sealed record DiskSample(IReadOnlyList<DiskReading> Disks, bool Incomplete);

/// <summary>One serially used PDH query, refreshed when the disk instance set changes.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class DiskReader(TimeProvider clock) : IDisposable
{
    private const int MaximumDisks = 256;
    private const uint DoubleWithoutScalingOrCap = 0x00009200;
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(5);
    private static readonly string[] Names = ["Disk Read Bytes/sec", "Disk Write Bytes/sec", "Disk Reads/sec",
        "Disk Writes/sec", "Avg. Disk sec/Read", "Avg. Disk sec/Write"];
    private QueryHandle? query;
    private List<(string Instance, nint[] Counters)> counters = [];
    private string[] knownPaths = [];
    private long? previousTimestamp;
    private long? discoveryTimestamp;
    private bool incomplete;

    public DiskSample Read(TimeSpan maximumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        try
        {
            var now = clock.GetTimestamp();
            if (query is null || discoveryTimestamp is null || clock.GetElapsedTime(discoveryTimestamp.Value, now) < TimeSpan.Zero ||
                clock.GetElapsedTime(discoveryTimestamp.Value, now) >= DiscoveryInterval)
            {
                var paths = PdhPaths.Expand(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
                Array.Sort(paths, StringComparer.Ordinal);
                if (query is null || !paths.SequenceEqual(knownPaths, StringComparer.Ordinal)) Initialize(paths);
                discoveryTimestamp = clock.GetTimestamp();
            }
            if (previousTimestamp is { } previous && (clock.GetElapsedTime(previous, now) <= TimeSpan.Zero ||
                clock.GetElapsedTime(previous, now) > maximumInterval))
            {
                Reset();
                Initialize(PdhPaths.Expand(@"\PhysicalDisk(*)\Disk Read Bytes/sec"));
                discoveryTimestamp = clock.GetTimestamp();
            }
            if (Collect(query!) != 0) throw new InvalidOperationException("Unable to collect Windows disk counters.");
            var warmed = previousTimestamp is not null;
            previousTimestamp = clock.GetTimestamp();
            if (!warmed) return new([], true);
            var readings = new List<DiskReading>(counters.Count);
            var missing = incomplete;
            Span<double?> values = stackalloc double?[6];
            foreach (var (name, handles) in counters)
            {
                values.Clear();
                for (var i = 0; i < handles.Length; i++)
                {
                    if (handles[i] == 0 || Format(handles[i], DoubleWithoutScalingOrCap, out _, out var value) != 0 ||
                        value.Status > 1 || !double.IsFinite(value.Value) || value.Value < 0)
                    {
                        missing = true;
                        continue;
                    }
                    values[i] = value.Value;
                }
                readings.Add(ToReading(name, values));
            }
            return new(readings, missing || readings.Count == 0);
        }
        catch
        {
            Reset();
            throw;
        }
    }

    internal static DiskReading ToReading(string name, ReadOnlySpan<double?> values) =>
        new(name, values[0], values[1],
            values[2] > 0 && values[4] is { } read ? read * 1000 : null,
            values[3] > 0 && values[5] is { } write ? write * 1000 : null);

    private void Initialize(string[] paths)
    {
        Reset();
        Array.Sort(paths, StringComparer.Ordinal);
        knownPaths = paths;
        if (OpenQuery(null, 0, out var raw) != 0) throw new InvalidOperationException("Unable to open Windows disk counters.");
        query = new QueryHandle(raw);
        foreach (var path in paths)
        {
            var start = path.IndexOf('(');
            var end = path.LastIndexOf(")\\", StringComparison.Ordinal);
            if (start < 0 || end <= start) { incomplete = true; continue; }
            var instance = path[(start + 1)..end];
            if (instance == "_Total") continue;
            if (instance.Length is 0 or > 256 || instance.Any(char.IsControl)) { incomplete = true; continue; }
            if (counters.Count == MaximumDisks) { incomplete = true; break; }
            var handles = new nint[Names.Length];
            for (var i = 0; i < Names.Length; i++)
                if (AddEnglish(query, $"\\PhysicalDisk({instance})\\{Names[i]}", 0, out handles[i]) != 0) incomplete = true;
            counters.Add((instance, handles));
        }
    }

    private void Reset()
    {
        query?.Dispose();
        query = null;
        counters.Clear();
        knownPaths = [];
        previousTimestamp = null;
        discoveryTimestamp = null;
        incomplete = false;
    }
    public void Dispose() => Reset();

    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle(nint handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseQuery(handle) == 0;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct FormattedValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint OpenQuery(string? source, nuint data, out nint query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint AddEnglish(QueryHandle query, string path, nuint data, out nint counter);
    [LibraryImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint Collect(QueryHandle query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint Format(nint counter, uint format, out uint type, out FormattedValue value);
    [LibraryImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint CloseQuery(nint query);
}
