using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

public enum HealthTargetStatus { Saved, NotFound, Conflict, Unavailable, Invalid, LimitReached }
public sealed record HealthTargetResult(HealthTargetStatus Status, HealthTargetSnapshot? Target = null);

/// <summary>Controls health target configuration and writes the corresponding audit in the same transaction.</summary>
public sealed class HealthTargetAdministration(IDbContextFactory<ServiceObservabilityDbContext> contexts,
    TimeProvider clock, IOperationAuditWriter<OperationAudit> audit)
{
    public const int MaxTargetsPerService = 100;
    public async Task<HealthTargetSnapshot?> GetAsync(Guid id, CancellationToken token)
    {
        if (id.Version != 7) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var target = await db.HealthTargets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return target is null ? null : Map(target);
    }

    public async Task<IReadOnlyList<HealthTargetSnapshot>> ListAsync(Guid serviceId, int page, CancellationToken token)
    {
        if (serviceId.Version != 7 || page is < 1 or > 1000) throw new ArgumentException("Invalid service or page.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.HealthTargets.AsNoTracking().Where(x => x.ServiceId == serviceId)
            .OrderBy(x => x.RowId).Skip((page - 1) * 50).Take(50).ToArrayAsync(token);
        return rows.Select(Map).ToArray();
    }

    public async Task<HealthTargetResult> CreateAsync(CreateHealthTargetInput input, Guid actor, CancellationToken token)
    {
        if (input is null || input.Settings is null || actor == Guid.Empty || input.ServiceId.Version != 7 ||
            (input.InstanceId is { } id && id.Version != 7) || !string.IsNullOrEmpty(input.Settings.Version))
            return new(HealthTargetStatus.Invalid);
        HealthTarget target;
        var now = clock.GetUtcNow();
        try
        {
            target = new(input.ServiceId, input.InstanceId, input.Settings.Endpoint, input.Settings.IntervalSeconds,
                input.Settings.TimeoutSeconds, input.Settings.FailureThreshold, input.Settings.RecoveryThreshold, now);
            target.SetEnabled(input.Settings.Enabled, now);
        }
        catch (ArgumentException) { return new(HealthTargetStatus.Invalid); }

        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var status = await LockServiceAsync(db, target.ServiceId, target.InstanceId, target.Enabled, token);
        if (status is not HealthTargetStatus.Saved) return new(status);
        if (await db.HealthTargets.CountAsync(x => x.ServiceId == target.ServiceId, token) >= MaxTargetsPerService)
            return new(HealthTargetStatus.LimitReached);
        var effectiveAt = await DiscoveryTransaction.NowAsync(db, token);
        db.HealthTargets.Add(target);
        db.HealthSchedules.Add(new HealthSchedule(target.Id, target.Enabled, effectiveAt));
        db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, target.Enabled ? (byte)0 : (byte)4, 0, effectiveAt));
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, target.Id, now, new HealthTargetChanged(HealthTargetAction.Created, target.ServiceId,
                null, AuditValues(target), "", Convert.ToBase64String(target.Version), true)), token);
        await transaction.CommitAsync(token);
        return new(HealthTargetStatus.Saved, Map(target));
    }

    public async Task<HealthTargetResult> UpdateAsync(Guid id, HealthTargetInput input, Guid actor, CancellationToken token)
    {
        if (id.Version != 7 || actor == Guid.Empty || input is null) return new(HealthTargetStatus.Invalid);
        byte[] version;
        try { version = Convert.FromBase64String(input.Version ?? ""); }
        catch (FormatException) { return new(HealthTargetStatus.Invalid); }
        if (version.Length != 8) return new(HealthTargetStatus.Invalid);

        await using var db = await contexts.CreateDbContextAsync(token);
        var identity = await db.HealthTargets.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.ServiceId, x.InstanceId }).SingleOrDefaultAsync(token);
        if (identity is null) return new(HealthTargetStatus.NotFound);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var status = await LockServiceAsync(db, identity.ServiceId, identity.InstanceId, input.Enabled, token);
        if (status is not HealthTargetStatus.Saved) return new(status);
        var target = await db.HealthTargets.FromSqlInterpolated($"SELECT * FROM [service].[HealthTargets] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
        if (target is null) return new(HealthTargetStatus.NotFound);
        var policy = await HealthInvalidation.LockPolicyAsync(db, token);
        var schedule = await db.HealthSchedules.FromSqlInterpolated($"SELECT * FROM [service].[HealthSchedules] WITH (UPDLOCK, HOLDLOCK) WHERE [TargetId] = {id}").SingleOrDefaultAsync(token);
        if (schedule is null) throw new InvalidOperationException("Health check scheduling state is missing.");
        if (!target.Version.AsSpan().SequenceEqual(version)) return new(HealthTargetStatus.Conflict);
        db.Entry(target).Property(x => x.Version).OriginalValue = version;
        var previous = AuditValues(target);
        var previousVersion = Convert.ToBase64String(target.Version);
        var previousEndpoint = target.Endpoint;
        var effectiveAt = await DiscoveryTransaction.NowAsync(db, token);
        var priorBoundary = schedule.State == 0 ? (effectiveAt, (byte)9)
            : await HealthInvalidation.BoundaryAsync(db, target, schedule, policy, effectiveAt, token);
        var now = clock.GetUtcNow();
        try
        {
            target.Apply(input.Endpoint, input.IntervalSeconds, input.TimeoutSeconds,
                input.FailureThreshold, input.RecoveryThreshold, now);
            target.SetEnabled(input.Enabled, now);
        }
        catch (ArgumentException) { return new(HealthTargetStatus.Invalid); }
        var changed = !string.Equals(previousEndpoint, target.Endpoint, StringComparison.Ordinal) ||
            previous != AuditValues(target);
        if (!changed) return new(HealthTargetStatus.Saved, Map(target));
        var interval = await db.HealthStateIntervals.FromSqlInterpolated($"SELECT * FROM [service].[HealthStateIntervals] WITH (UPDLOCK, HOLDLOCK) WHERE [TargetId] = {id} AND [EndedAt] IS NULL").SingleOrDefaultAsync(token);
        var boundary = priorBoundary.Item1;
        if (interval is not null && boundary < interval.StartedAt) boundary = interval.StartedAt;
        if (boundary > effectiveAt) effectiveAt = boundary;
        if (interval is not null) interval.Close(boundary);
        schedule.Reset(target.Enabled, effectiveAt);
        db.Entry(target).Property(x => x.UpdatedAt).IsModified = true;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return new(HealthTargetStatus.Conflict); }
        if (interval is not null && boundary < effectiveAt)
        {
            var gap = new HealthStateInterval(id, 0, priorBoundary.Item2, boundary);
            gap.Close(effectiveAt);
            db.HealthStateIntervals.Add(gap);
        }
        db.HealthStateIntervals.Add(new HealthStateInterval(id, target.Enabled ? (byte)0 : (byte)4, 9, effectiveAt));
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, id, now, new HealthTargetChanged(HealthTargetAction.Updated, target.ServiceId,
                previous, AuditValues(target), previousVersion, Convert.ToBase64String(target.Version),
                !string.Equals(previousEndpoint, target.Endpoint, StringComparison.Ordinal))), token);
        await transaction.CommitAsync(token);
        return new(HealthTargetStatus.Saved, Map(target));
    }

    internal static async Task<HealthTargetStatus> LockServiceAsync(ServiceObservabilityDbContext db, Guid serviceId,
        Guid? instanceId, bool enabling, CancellationToken token)
    {
        var sourceId = await db.Services.AsNoTracking().Where(x => x.Id == serviceId).Select(x => (Guid?)x.SourceId).SingleOrDefaultAsync(token);
        if (sourceId is null) return HealthTargetStatus.NotFound;
        var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId.Value}").SingleOrDefaultAsync(token);
        var service = await db.Services.FromSqlInterpolated($"SELECT * FROM [service].[Services] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {serviceId}").SingleOrDefaultAsync(token);
        if (source is null || service is null) return HealthTargetStatus.NotFound;
        if (enabling && (source.RevokedAt is not null || service.RevokedAt is not null)) return HealthTargetStatus.Unavailable;
        if (instanceId is { } id)
        {
            var instance = await db.Instances.FromSqlInterpolated($"SELECT * FROM [service].[Instances] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id} AND [ServiceId] = {serviceId}").SingleOrDefaultAsync(token);
            if (instance is null) return HealthTargetStatus.NotFound;
            if (enabling && instance.RetiredAt is not null) return HealthTargetStatus.Unavailable;
        }
        return HealthTargetStatus.Saved;
    }

    private static HealthTargetSnapshot Map(HealthTarget target) => new(target.Id, target.ServiceId, target.InstanceId,
        target.Endpoint, target.IntervalSeconds, target.TimeoutSeconds, target.FailureThreshold,
        target.RecoveryThreshold, target.Enabled, Convert.ToBase64String(target.Version));

    private static HealthTargetAuditValues AuditValues(HealthTarget target) => new(target.IntervalSeconds, target.TimeoutSeconds,
        target.FailureThreshold, target.RecoveryThreshold, target.Enabled);
}
