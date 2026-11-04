using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Commits all accepted trace groups in one transaction and admits quota once.</summary>
internal sealed class TraceExport(IDbContextFactory<ServiceObservabilityDbContext> contexts, CandidateDiscovery candidates)
{
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private const int MaximumGroups = 256;
    private const int MaximumSpans = 10_000;
    private sealed record Group(ResourceSpans Resource, ScopeSpans Scope, ResourceIdentity Identity);

    internal async Task<ExportResult> WriteAsync(Guid authenticatedSourceId, ExportTraceServiceRequest request, CancellationToken token, byte[]? version = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (authenticatedSourceId.Version != 7 || request.CalculateSize() > MaximumRequestBytes ||
            request.ResourceSpans.Sum(x => (long)x.ScopeSpans.Count) > MaximumGroups ||
            request.ResourceSpans.Sum(x => x.ScopeSpans.Sum(y => (long)y.Spans.Count)) > MaximumSpans) return new(ExportStatus.Invalid, 0);
        long rejected = 0;
        var groups = new List<Group>();
        foreach (var resource in request.ResourceSpans)
        {
            if (!ResourceIdentity.TryRead(resource.Resource, out var identity) || identity is null)
            { rejected += resource.ScopeSpans.Sum(x => (long)x.Spans.Count); continue; }
            foreach (var scope in resource.ScopeSpans)
            {
                if (scope.Spans.Count == 0) continue;
                if (!TraceIngestion.ValidGroup(resource, scope)) rejected += scope.Spans.Count;
                else groups.Add(new(resource, scope, identity));
            }
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
        var quota = new List<TraceSource>();
        foreach (var group in groups)
        {
            var key = ServiceKey.From(group.Identity);
            var binding = bindings[key];
            if (binding is null || TraceIngestion.CheckTimes(group.Scope, binding.ApprovedAt, received) != TraceIngestionStatus.Accepted)
            { rejected += group.Scope.Spans.Count; continue; }
            var instance = await ServiceInstanceResolver.ResolveAsync(connection, transaction, authenticatedSourceId,
                key.Name, key.Environment, group.Identity.InstanceId, token, key.Namespace);
            if (instance.Status is InstanceResolutionStatus.Busy or InstanceResolutionStatus.CapacityReached) return new(ExportStatus.Retry, 0);
            if (instance.Status is not (InstanceResolutionStatus.Resolved or InstanceResolutionStatus.Unidentified))
            { rejected += group.Scope.Spans.Count; continue; }
            TraceIngestion.Stage(db, group.Resource, group.Scope, instance, received);
            quota.Add(new(authenticatedSourceId, binding.ServiceId, group.Scope.Spans.Count));
        }
        if (quota.Count > 0)
        {
            var admission = await TraceAdmission.AdmitAsync(connection, transaction, quota, token);
            if (admission.Status == LogAdmissionStatus.BatchTooLarge) return new(ExportStatus.BatchTooLarge, 0);
            if (admission.Status != LogAdmissionStatus.Accepted) return new(ExportStatus.Retry, 0, admission.RetryAfter);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
        }
        return new(rejected == 0 ? ExportStatus.Complete : ExportStatus.Partial, rejected);
    }
}
