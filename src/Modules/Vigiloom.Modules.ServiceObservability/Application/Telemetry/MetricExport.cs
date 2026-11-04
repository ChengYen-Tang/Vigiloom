using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Commits all accepted metric points in an OTLP request as one transaction.</summary>
internal sealed class MetricExport(IDbContextFactory<ServiceObservabilityDbContext> contexts, CandidateDiscovery candidates)
{
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private const int MaximumGroups = 256;
    private const int MaximumPoints = 10_000;
    private sealed record Group(ResourceMetrics Resource, ScopeMetrics Scope, ResourceIdentity Identity, MetricIngestion.MetricGroup Data);

    internal async Task<ExportResult> WriteAsync(Guid authenticatedSourceId, ExportMetricsServiceRequest request, CancellationToken token, byte[]? version = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (authenticatedSourceId.Version != 7 || request.CalculateSize() > MaximumRequestBytes ||
            request.ResourceMetrics.Sum(x => x.ScopeMetrics.Sum(y => (long)y.Metrics.Count)) > MaximumGroups)
            return new(ExportStatus.Invalid, 0);
        long totalPoints = 0;
        foreach (var resource in request.ResourceMetrics)
        foreach (var scope in resource.ScopeMetrics)
        foreach (var metric in scope.Metrics)
        {
            var count = Count(metric);
            if (count < 0 || (totalPoints += count) > MaximumPoints) return new(ExportStatus.Invalid, 0);
        }
        long rejected = 0;
        var groups = new List<Group>();
        foreach (var resource in request.ResourceMetrics)
        foreach (var scope in resource.ScopeMetrics)
        foreach (var metric in scope.Metrics)
        {
            var count = Count(metric);
            if (count < 0) return new(ExportStatus.Invalid, 0);
            if (count == 0) continue;
            if (!ResourceIdentity.TryRead(resource.Resource, out var identity) || identity is null ||
                !MetricIngestion.TryPrepare(resource, scope, metric, out var data) || data is null)
                rejected += count;
            else groups.Add(new(resource, scope, identity, data));
        }
        var keys = BatchAuthorization.Order(groups.Select(x => x.Identity));
        foreach (var key in keys)
        {
            var observation = version is null
                ? await candidates.ObserveAsync(authenticatedSourceId, key.Name, key.Environment, key.Name, token, key.Namespace)
                : await candidates.ObserveAsync(new AuthenticatedSource(authenticatedSourceId, version), key.Name, key.Environment, key.Name, token, key.Namespace);
            if (observation is CandidateObservation.Busy or CandidateObservation.CapacityReached)
                return new(ExportStatus.Retry, 0, TimeSpan.FromMinutes(1));
            if (observation is CandidateObservation.SourceUnavailable) return new(ExportStatus.Unauthorized, 0);
        }
        if (groups.Count == 0) return new(rejected == 0 ? ExportStatus.Complete : ExportStatus.Partial, rejected);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var connection = db.Database.GetDbConnection();
        var transaction = tx.GetDbTransaction();
        var authorization = await BatchAuthorization.LockAsync(db, tx, authenticatedSourceId, keys, token, version);
        if (authorization.SourceUnavailable) return new(ExportStatus.Unauthorized, 0);
        var bindings = authorization.Bindings;
        if (bindings is null) return new(ExportStatus.Retry, 0);
        var received = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var accepted = 0;
        foreach (var group in groups)
        {
            var key = ServiceKey.From(group.Identity);
            var binding = bindings[key];
            if (binding is null || MetricIngestion.CheckTimes(group.Data, binding.ApprovedAt, received) != MetricIngestionStatus.Accepted)
            { rejected += group.Data.Points.Count; continue; }
            var instance = await ServiceInstanceResolver.ResolveAsync(connection, transaction, authenticatedSourceId,
                key.Name, key.Environment, group.Identity.InstanceId, token, key.Namespace);
            if (instance.Status is InstanceResolutionStatus.Busy or InstanceResolutionStatus.CapacityReached) return new(ExportStatus.Retry, 0);
            if (instance.Status is not (InstanceResolutionStatus.Resolved or InstanceResolutionStatus.Unidentified))
            { rejected += group.Data.Points.Count; continue; }
            MetricIngestion.Stage(db, group.Resource, group.Scope, group.Data, instance, received);
            accepted += group.Data.Points.Count;
        }
        if (accepted > 0)
        {
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
        }
        return new(rejected == 0 ? ExportStatus.Complete : ExportStatus.Partial, rejected);
    }

    private static int Count(Metric metric) => metric.DataCase switch
    {
        Metric.DataOneofCase.Gauge => metric.Gauge.DataPoints.Count,
        Metric.DataOneofCase.Sum => metric.Sum.DataPoints.Count,
        Metric.DataOneofCase.Histogram => metric.Histogram.DataPoints.Count,
        Metric.DataOneofCase.ExponentialHistogram => metric.ExponentialHistogram.DataPoints.Count,
        Metric.DataOneofCase.Summary => metric.Summary.DataPoints.Count,
        _ => -1
    };
}
