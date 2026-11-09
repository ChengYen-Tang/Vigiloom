using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Platforms.Windows.Metrics;

public sealed record CpuCoreSample(IReadOnlyDictionary<string, double> Cores, double? Total, bool Incomplete);

/// <summary>Windows PDH processor information; one reader belongs to one serialized sampler.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class CpuCoreReader(TimeProvider clock) : IDisposable
{
    private const int MaximumProcessors = 4096;
    private const uint FormatDouble = 0x00000200;
    private QueryHandle? query;
    private List<(string Name, nint Handle)> counters = [];
    private uint[] processorCounts = [];
    private long? previousTimestamp;
    private bool incomplete;

    public CpuCoreSample Read(TimeSpan maximumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        var now = clock.GetTimestamp();
        if (query is not null && TopologyChanged()) Reset();
        if (previousTimestamp is { } previous && (clock.GetElapsedTime(previous, now) <= TimeSpan.Zero ||
            clock.GetElapsedTime(previous, now) > maximumInterval)) Reset();
        try
        {
            if (query is null) Initialize();
            if (Collect(query!) != 0) throw new InvalidOperationException("Unable to collect Windows CPU counters.");
            var elapsedSample = previousTimestamp is not null;
            previousTimestamp = clock.GetTimestamp();
            if (!elapsedSample) return new(new Dictionary<string, double>(), null, true);
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            double? total = null;
            var failed = incomplete;
            foreach (var (name, handle) in counters)
            {
                if (Format(handle, FormatDouble, out _, out var formatted) != 0 || formatted.Status > 1 ||
                    !double.IsFinite(formatted.Value) || formatted.Value < 0 || formatted.Value > 100)
                {
                    failed = true;
                    continue;
                }
                if (name == "_Total") total = formatted.Value;
                else values.Add(name, formatted.Value);
            }
            return new(values, total, failed || values.Count == 0 || total is null);
        }
        catch
        {
            Reset();
            throw;
        }
    }

    private void Initialize()
    {
        if (OpenQuery(null, 0, out var opened) != 0) throw new InvalidOperationException("Unable to open Windows CPU counters.");
        query = new QueryHandle(opened);
        var groups = GetActiveProcessorGroupCount();
        if (groups == 0) throw new InvalidOperationException("No processor groups are available.");
        processorCounts = new uint[groups];
        var count = 0;
        for (ushort group = 0; group < groups; group++)
        {
            var processors = GetActiveProcessorCount(group);
            if (processors == 0) throw new InvalidOperationException("A processor group has no active processors.");
            processorCounts[group] = processors;
            for (uint index = 0; index < processors; index++)
            {
                if (count++ == MaximumProcessors) { incomplete = true; break; }
                var name = string.Create(CultureInfo.InvariantCulture, $"{group},{index}");
                Add(name);
            }
            if (count > MaximumProcessors) break;
        }
        Add("_Total");
    }

    private bool TopologyChanged()
    {
        var groups = GetActiveProcessorGroupCount();
        if (groups == 0 || groups != processorCounts.Length) return true;
        for (ushort group = 0; group < groups; group++)
            if (GetActiveProcessorCount(group) != processorCounts[group]) return true;
        return false;
    }

    private void Add(string name)
    {
        var path = $"\\Processor Information({name})\\% Processor Time";
        if (AddEnglish(query!, path, 0, out var counter) == 0) counters.Add((name, counter));
        else incomplete = true;
    }

    private void Reset()
    {
        query?.Dispose();
        query = null;
        counters.Clear();
        processorCounts = [];
        previousTimestamp = null;
        incomplete = false;
    }

    public void Dispose() => Reset();

    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle(nint handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseQuery(handle) == 0;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FormattedValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

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
    [LibraryImport("kernel32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial ushort GetActiveProcessorGroupCount();
    [LibraryImport("kernel32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint GetActiveProcessorCount(ushort group);
}
