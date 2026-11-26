using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Logs.V1;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Admission = Vigiloom.Infrastructure.LogAdmission.LogAdmission;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

public enum ExportStatus { Complete, Partial, Retry, Invalid, BatchTooLarge, Unauthorized }
public sealed record ExportResult(ExportStatus Status, long Rejected, TimeSpan? RetryAfter = null, bool ServiceNotAuthorized = false, bool MetricWindowPruned = false);

/// <summary>Atomically commits all accepted groups in an OTLP Log request.</summary>
internal sealed class LogExport(IDbContextFactory<ServiceObservabilityDbContext> contexts, ICandidateObserver candidates,
    IApplicationLogDelivery? delivery = null)
{
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private const int MaximumGroups = 256;
    private const int MaximumRecords = 10_000;
    private sealed record Group(ResourceLogs Resource, ScopeLogs Scope, ResourceIdentity Identity);

    internal async Task<ExportResult> WriteAsync(Guid authenticatedSourceId, ExportLogsServiceRequest request, CancellationToken token, byte[]? version = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (authenticatedSourceId.Version != 7 || request.CalculateSize() > MaximumRequestBytes ||
            request.ResourceLogs.Sum(x => (long)x.ScopeLogs.Count) > MaximumGroups ||
            request.ResourceLogs.Sum(x => x.ScopeLogs.Sum(y => (long)y.LogRecords.Count)) > MaximumRecords)
            return new(ExportStatus.Invalid, 0);
        long rejected = 0;
        var groups = new List<Group>();
        foreach (var resource in request.ResourceLogs)
        {
            if (!ResourceIdentity.TryRead(resource.Resource, out var identity) || identity is null)
            {
                rejected += resource.ScopeLogs.Sum(x => (long)x.LogRecords.Count);
                continue;
            }
            foreach (var scope in resource.ScopeLogs)
            {
                if (scope.LogRecords.Count == 0) continue;
                if (!LogIngestion.ValidGroup(resource, scope)) rejected += scope.LogRecords.Count;
                else groups.Add(new(resource, scope, identity));
            }
        }
        var keys = BatchAuthorization.Order(groups.Select(x => x.Identity));
        // Discovery uses a different lock order; complete it before any formal write transaction.
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
        var serviceNotAuthorized = false;
        var remoteGroups = delivery is null ? null : new Dictionary<ResourceLogs, List<ApplicationLogScope>>(ReferenceEqualityComparer.Instance);
        foreach (var group in groups)
        {
            var key = ServiceKey.From(group.Identity);
            var binding = bindings[key];
            if (binding is null)
            { rejected += group.Scope.LogRecords.Count; serviceNotAuthorized = true; continue; }
            if (LogIngestion.CheckTimes(group.Scope, binding.ApprovedAt, received) != LogIngestionStatus.Accepted)
            { rejected += group.Scope.LogRecords.Count; continue; }
            var instance = await ServiceInstanceResolver.ResolveAsync(connection, transaction, authenticatedSourceId,
                key.Name, key.Environment, group.Identity.InstanceId, token, key.Namespace);
            if (instance.Status is InstanceResolutionStatus.Busy or InstanceResolutionStatus.CapacityReached) return new(ExportStatus.Retry, 0);
            if (instance.Status is not (InstanceResolutionStatus.Resolved or InstanceResolutionStatus.Unidentified))
            { rejected += group.Scope.LogRecords.Count; continue; }
            if (remoteGroups is null) _ = LogIngestion.Stage(db, group.Resource, group.Scope, instance, received);
            else
            {
                if (!remoteGroups.TryGetValue(group.Resource, out var scopes))
                    remoteGroups.Add(group.Resource, scopes = []);
                scopes.Add(new ApplicationLogScope(binding.ServiceId, instance.InstanceId, group.Scope.Scope?.ToByteArray() ?? [],
                    group.Scope.SchemaUrl, group.Scope.LogRecords.Select(log => LogIngestion.ToItem(log, received)).ToArray()));
            }
            accepted += group.Scope.LogRecords.Count;
        }
        if (accepted > 0)
        {
            if (remoteGroups is not null)
            {
                var resources = remoteGroups.Select(entry => new ApplicationLogResource(entry.Key.Resource.ToByteArray(),
                    entry.Key.SchemaUrl, entry.Value)).ToArray();
                var receipt = await delivery!.SendAsync(new ApplicationLogBatch(authenticatedSourceId, resources), token);
                if (receipt.Status != LogReceiptStatus.Accepted)
                    return receipt.Status switch
                    {
                        LogReceiptStatus.BatchTooLarge => new(ExportStatus.BatchTooLarge, 0),
                        LogReceiptStatus.Invalid => new(ExportStatus.Invalid, 0),
                        _ => new(ExportStatus.Retry, 0, receipt.RetryAfter)
                    };
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
                return new(rejected == 0 ? ExportStatus.Complete : ExportStatus.Partial, rejected, ServiceNotAuthorized: serviceNotAuthorized);
            }
            var admission = await Admission.AdmitAsync(connection, transaction, new(LogSourceKind.Application, authenticatedSourceId), accepted, token);
            if (admission.Status == LogAdmissionStatus.BatchTooLarge) return new(ExportStatus.BatchTooLarge, 0);
            if (admission.Status != LogAdmissionStatus.Accepted) return new(ExportStatus.Retry, 0, admission.RetryAfter);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
        }
        return new(rejected == 0 ? ExportStatus.Complete : ExportStatus.Partial, rejected, ServiceNotAuthorized: serviceNotAuthorized);
    }
}
