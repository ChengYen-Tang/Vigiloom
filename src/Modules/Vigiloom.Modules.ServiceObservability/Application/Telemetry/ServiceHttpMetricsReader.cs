using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Conservatively summarizes one current window of received HTTP histograms.</summary>
public sealed class ServiceHttpMetricsReader(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private const int MaximumSeries = 200;
    private const int MaximumPayloadBytes = 4 * 1024 * 1024;
    private const long HarmlessQuality = (long)(MetricRollupQuality.StatisticsUnverified | MetricRollupQuality.ExemplarsRequireRaw);
    private static readonly SemaphoreSlim ReadGate = new(2, 2);

    public async Task<ServiceHttpMetrics> ReadAsync(Guid serviceId, CancellationToken token)
    {
        if (serviceId.Version != 7) throw new ArgumentException("Invalid service.", nameof(serviceId));
        if (!await ReadGate.WaitAsync(0, token)) return Unavailable("Busy");
        try { return await ReadCoreAsync(serviceId, token); }
        finally { ReadGate.Release(); }
    }

    private async Task<ServiceHttpMetrics> ReadCoreAsync(Guid serviceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var cutoff = (await DiscoveryTransaction.NowAsync(db, token)).AddMinutes(-5);
        var window = await db.MetricRollupWindows.AsNoTracking().Where(x => x.ServiceId == serviceId && x.WindowStart <= cutoff)
            .OrderByDescending(x => x.WindowStart).FirstOrDefaultAsync(token);
        if (window is null) return Unavailable("NoData");
        var sourceId = await db.Services.AsNoTracking().Where(x => x.Id == serviceId).Select(x => (Guid?)x.SourceId).SingleOrDefaultAsync(token);
        if (sourceId is null) return Unavailable("Incomplete");
        MetricRollupSeries[] series;
        if (!window.RawPruned)
        {
            var ticks = (ulong)(window.WindowStart - DateTimeOffset.UnixEpoch).Ticks;
            if (ticks > ulong.MaxValue / 100 || !MetricWindow.TryForSample(ticks * 100, out var metricWindow))
                return Unavailable("Incomplete");
            var raw = await MetricWindowInputs.ReadAsync(db, serviceId, metricWindow, token, maxPoints: 1000, maxBytes: MaximumPayloadBytes);
            if (raw.CapacityExceeded) return Unavailable("Incomplete");
            var grouped = MetricWindowGrouping.Group(sourceId.Value, serviceId, metricWindow, raw.Points);
            if (grouped.RequiresRaw || grouped.Series.Count > MaximumSeries) return Unavailable("Incomplete");
            var relevant = grouped.Series.Where(x => x.Metadata.Name == "http.server.request.duration").ToArray();
            var results = relevant.Select(x => MetricRollupEncoding.Encode(x, metricWindow)).ToArray();
            if (results.Any(x => x?.ResultPayload is null)) return Unavailable("Incomplete");
            series = results.Select(x => x!).ToArray();
        }
        else
        {
            if (window.ProjectedRevision != window.InputRevision || window.ProjectionVersion != MetricRollupMaterializer.ProjectionVersion || !window.IsComplete)
                return Unavailable("Incomplete");
            var metadata = await db.MetricRollupSeries.AsNoTracking().Where(x => x.WindowRowId == window.RowId)
                .OrderBy(x => x.RowId).Take(MaximumSeries + 1)
                .Select(x => new { x.RowId, Size = x.ResultPayload == null ? (int?)null : x.ResultPayload.Length }).ToArrayAsync(token);
            if (metadata.Length > MaximumSeries || metadata.Any(x => x.Size is null) || metadata.Sum(x => (long)x.Size.GetValueOrDefault()) > MaximumPayloadBytes)
                return Unavailable("Incomplete");
            var ids = metadata.Select(x => x.RowId).ToArray();
            series = await db.MetricRollupSeries.AsNoTracking().Where(x => ids.Contains(x.RowId))
                .Select(x => new MetricRollupSeries { InstanceId = x.InstanceId, Mode = x.Mode, QualityFlags = x.QualityFlags,
                    ResultVersion = x.ResultVersion, ResultPayload = x.ResultPayload, IdentityHash = Array.Empty<byte>(), IdentityBytes = Array.Empty<byte>() })
                .ToArrayAsync(token);
            if (series.Length != metadata.Length) return Unavailable("Changed");
        }
        var version = await db.MetricRollupWindows.AsNoTracking().Where(x => x.RowId == window.RowId).Select(x => x.Version).SingleOrDefaultAsync(token);
        if (version is null || !version.AsSpan().SequenceEqual(window.Version)) return Unavailable("Changed");

        long count = 0, errors = 0;
        double sum = 0;
        bool found = false, errorsKnown = true;
        ulong? commonFrom = null, commonTo = null;
        string? scopeIdentity = null;
        var instanceProfiles = new Dictionary<Guid, (ByteString Identity, string Keys)>();
        var observedDimensions = new HashSet<(Guid Instance, string Attributes)>();
        try
        {
            foreach (var row in series)
            {
                if (row.ResultVersion != 1 || row.ResultPayload is null) return Unavailable("Incomplete");
                var resource = ResourceMetrics.Parser.ParseFrom(row.ResultPayload);
                foreach (var scope in resource.ScopeMetrics)
                foreach (var metric in scope.Metrics)
                {
                    if (metric.Name != "http.server.request.duration") continue;
                    found = true;
                    if (metric.Unit != "s" || row.InstanceId is null || metric.DataCase != Metric.DataOneofCase.Histogram ||
                        (row.QualityFlags & ~HarmlessQuality) != 0) return Unavailable("Incomplete");
                    if (string.IsNullOrWhiteSpace(scope.Scope?.Name)) return Unavailable("AmbiguousSource");
                    var currentScope = $"{scope.Scope?.Name}\0{scope.Scope?.Version}";
                    if (scopeIdentity is not null && scopeIdentity != currentScope) return Unavailable("AmbiguousSource");
                    scopeIdentity = currentScope;
                    var points = metric.Histogram.DataPoints;
                    HistogramDataPoint first, last;
                    if (row.Mode == MetricRollupMode.DeltaHistogram && points.Count == 1 &&
                        metric.Histogram.AggregationTemporality == AggregationTemporality.Delta)
                    {
                        var windowStart = checked((ulong)window.WindowStart.ToUnixTimeMilliseconds() * 1_000_000);
                        var windowEnd = checked((ulong)window.WindowStart.AddMinutes(5).ToUnixTimeMilliseconds() * 1_000_000);
                        if (points[0].StartTimeUnixNano != windowStart || points[0].TimeUnixNano != windowEnd)
                            return Unavailable("Incomplete");
                        first = new HistogramDataPoint { TimeUnixNano = points[0].StartTimeUnixNano, Count = 0, Sum = 0 };
                        last = points[0];
                    }
                    else if (row.Mode == MetricRollupMode.CumulativeHistogramSnapshots && points.Count == 2 &&
                        metric.Histogram.AggregationTemporality == AggregationTemporality.Cumulative &&
                        points[0].StartTimeUnixNano != 0 && points[0].StartTimeUnixNano == points[1].StartTimeUnixNano)
                    { first = points[0]; last = points[1]; }
                    else return Unavailable("Incomplete");
                    var attributes = last.Attributes.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
                    if (attributes.Any(x => x.Value is null) || attributes.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != attributes.Length)
                        return Unavailable("AmbiguousSource");
                    var keys = string.Join("\0", attributes.Where(x => x.Key is not ("error.type" or "http.response.status_code" or "http.route" or "aspnetcore.request.is_unhandled" or "network.protocol.version"))
                        .Select(x => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x.Key))));
                    var values = string.Join("\0", attributes.Select(x => Convert.ToBase64String(x.ToByteArray())));
                    var instance = row.InstanceId.Value;
                    var metricMetadata = metric.Clone();
                    metricMetadata.Histogram.DataPoints.Clear();
                    if (resource.Resource is null || !MetricSeriesIdentity.TryCreate(sourceId.Value, serviceId, instance, resource.Resource,
                        scope.Scope, resource.SchemaUrl, scope.SchemaUrl, metricMetadata, [], out var identity) || identity is null)
                        return Unavailable("AmbiguousSource");
                    var profile = (identity.Encoded, keys);
                    if (instanceProfiles.TryGetValue(instance, out var existing) && existing != profile) return Unavailable("AmbiguousSource");
                    instanceProfiles[instance] = profile;
                    if (!observedDimensions.Add((instance, values))) return Unavailable("AmbiguousSource");
                    if (first.TimeUnixNano >= last.TimeUnixNano || !first.HasSum || !last.HasSum ||
                        !double.IsFinite(first.Sum) || !double.IsFinite(last.Sum) || first.Sum < 0 || last.Sum < first.Sum || last.Count < first.Count)
                        return Unavailable("Incomplete");
                    commonFrom ??= first.TimeUnixNano;
                    commonTo ??= last.TimeUnixNano;
                    if (commonFrom != first.TimeUnixNano || commonTo != last.TimeUnixNano) return Unavailable("Incomplete");
                    var difference = checked((long)(last.Count - first.Count));
                    if (difference == 0 && last.Sum != first.Sum) return Unavailable("Incomplete");
                    count = checked(count + difference);
                    sum += last.Sum - first.Sum;
                    if (!double.IsFinite(sum)) return Unavailable("Incomplete");
                    var error = IsError(last.Attributes);
                    if (error is null) errorsKnown = false;
                    else if (error.Value) errors = checked(errors + difference);
                }
            }
            if (!found) return Unavailable("NoData");
            var from = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(commonFrom!.Value / 1_000_000)));
            var to = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(commonTo!.Value / 1_000_000)));
            if (sum > double.MaxValue / 1000) return Unavailable("Incomplete");
            return new("Available", from, to, count, count > 0 ? sum * 1000 / count : null,
                count > 0 && errorsKnown ? (double)errors / count : null);
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or OverflowException or ArgumentOutOfRangeException)
        { return Unavailable("Incomplete"); }
    }

    private static bool? IsError(IEnumerable<KeyValue> attributes)
    {
        long? status = null;
        bool error = false;
        foreach (var item in attributes)
        {
            if (item.Key == "http.response.status_code" && item.Value?.ValueCase == AnyValue.ValueOneofCase.IntValue) status = item.Value.IntValue;
            if (item.Key == "error.type" && item.Value?.ValueCase == AnyValue.ValueOneofCase.StringValue && !string.IsNullOrEmpty(item.Value.StringValue)) error = true;
        }
        if (error || status is >= 500 and <= 599) return true;
        return status is >= 100 and <= 499 ? false : null;
    }

    private static ServiceHttpMetrics Unavailable(string state) => new(state, null, null, null, null, null);
}
