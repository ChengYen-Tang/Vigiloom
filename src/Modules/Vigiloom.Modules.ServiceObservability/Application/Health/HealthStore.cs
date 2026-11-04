using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

internal sealed record HealthLease(Guid TargetId, Guid Owner, long Generation);
internal sealed record HealthWork(HealthLease Lease, Guid SourceId, Guid ServiceId, Guid? InstanceId, string Endpoint,
    int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, int RecoveryThreshold,
    byte[] SourceVersion, byte[] ServiceVersion, byte[]? InstanceVersion, byte[] TargetVersion);
internal sealed record HealthNetworkSnapshot(Guid Id, long Generation, byte[] Version, IReadOnlyList<string> Networks, long TransportGeneration = 0);

/// <summary>Claims bounded work and fences result writes against current authorization and settings.</summary>
internal sealed class HealthStore(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    internal async Task<HealthNetworkSnapshot?> NetworkAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var policy = await db.HealthNetworkPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.Name == "central", token);
        return policy is null ? null : new(policy.Id, policy.Generation, policy.Version, HealthNetworkPolicy.ParseNetworks(policy.NetworksJson));
    }

    internal async Task<bool> IsNetworkCurrentAsync(HealthNetworkSnapshot snapshot, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var version = await db.HealthNetworkPolicies.AsNoTracking().Where(x => x.Id == snapshot.Id && x.Generation == snapshot.Generation)
            .Select(x => x.Version).SingleOrDefaultAsync(token);
        return version is not null && version.AsSpan().SequenceEqual(snapshot.Version);
    }

    internal async Task<DateTimeOffset> NowAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await DiscoveryTransaction.NowAsync(db, token);
    }

    internal async Task<IReadOnlyList<HealthLease>> ClaimAsync(int count, CancellationToken token)
    {
        if (count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(count));
        var owner = Guid.CreateVersion7();
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
            ;WITH due AS
            (
                SELECT TOP (@count) * FROM [service].[HealthSchedules] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE [Enabled] = 1 AND [DueAt] <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                  AND ([LeaseUntil] IS NULL OR [LeaseUntil] <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                ORDER BY [DueAt], [RowId]
            )
            UPDATE due SET [Generation] = [Generation] + 1, [LeaseOwner] = @owner,
                [LeaseUntil] = DATEADD(second, 180, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
            OUTPUT inserted.[TargetId], inserted.[Generation]
            """;
            Add(command, "@count", DbType.Int32, count);
            Add(command, "@owner", DbType.Guid, owner);
            var leases = new List<HealthLease>(count);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) leases.Add(new(reader.GetGuid(0), owner, reader.GetInt64(1)));
            return leases;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    internal async Task<HealthWork?> LoadAsync(HealthLease lease, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var current = await db.HealthSchedules.FromSqlInterpolated($"SELECT * FROM [service].[HealthSchedules] WHERE [TargetId] = {lease.TargetId} AND [LeaseOwner] = {lease.Owner} AND [Generation] = {lease.Generation} AND [Enabled] = 1 AND [LeaseUntil] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')")
            .AsNoTracking().AnyAsync(token);
        if (!current) return null;
        var target = await db.HealthTargets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lease.TargetId && x.Enabled, token);
        if (target is null) return null;
        var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target.ServiceId && x.RevokedAt == null, token);
        if (service is null) return null;
        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == service.SourceId && x.RevokedAt == null, token);
        if (source is null) return null;
        byte[]? instanceVersion = null;
        if (target.InstanceId is { } id)
        {
            var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ServiceId == target.ServiceId && x.RetiredAt == null, token);
            if (instance is null) return null;
            instanceVersion = instance.Version;
        }
        return new(lease, source.Id, target.ServiceId, target.InstanceId, target.Endpoint, target.IntervalSeconds,
            target.TimeoutSeconds, target.FailureThreshold, target.RecoveryThreshold,
            source.Version, service.Version, instanceVersion, target.Version);
    }

    internal async Task DeferAsync(HealthLease lease, int seconds, CancellationToken token)
    {
        if (seconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [LeaseOwner] = NULL, [LeaseUntil] = NULL, [DueAt] = DATEADD(second, {seconds}, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [TargetId] = {lease.TargetId} AND [LeaseOwner] = {lease.Owner} AND [Generation] = {lease.Generation}", token);
    }

    internal async Task ExpireDueAsync(CancellationToken token)
    {
        Guid[] due;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            due = await db.Database.SqlQueryRaw<Guid>("""
                SELECT TOP (32) s.[TargetId] AS [Value]
                FROM [service].[HealthSchedules] AS s
                INNER JOIN [service].[HealthTargets] AS t ON t.[Id] = s.[TargetId]
                WHERE s.[Enabled] = 1 AND t.[Enabled] = 1 AND s.[State] <> 0 AND s.[EvidenceAt] IS NOT NULL
                  AND (s.[LeaseUntil] IS NULL OR s.[LeaseUntil] <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                  AND DATEADD(second, 2 * t.[IntervalSeconds] + t.[TimeoutSeconds], s.[EvidenceAt]) <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                ORDER BY s.[EvidenceAt], s.[RowId]
                """).ToArrayAsync(token);
        }
        foreach (var id in due) await ExpireAsync(id, token);
    }

    private async Task ExpireAsync(Guid targetId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var identity = await db.HealthTargets.AsNoTracking().Where(x => x.Id == targetId)
            .Select(x => new { x.ServiceId, x.InstanceId }).SingleOrDefaultAsync(token);
        if (identity is null) return;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await HealthTargetAdministration.LockServiceAsync(db, identity.ServiceId, identity.InstanceId, false, token) != HealthTargetStatus.Saved) return;
        var target = await db.HealthTargets.FromSqlInterpolated($"SELECT * FROM [service].[HealthTargets] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {targetId}").SingleOrDefaultAsync(token);
        if (target is null || !target.Enabled) return;
        var policy = await db.HealthNetworkPolicies.FromSqlRaw("SELECT * FROM [service].[HealthNetworkPolicies] WITH (HOLDLOCK) WHERE [Name] = 'central'").SingleOrDefaultAsync(token);
        var schedule = await db.HealthSchedules.FromSqlInterpolated($"SELECT * FROM [service].[HealthSchedules] WITH (UPDLOCK, HOLDLOCK) WHERE [TargetId] = {targetId}").SingleOrDefaultAsync(token);
        if (schedule is null || !schedule.Enabled) return;
        var now = await DiscoveryTransaction.NowAsync(db, token);
        if (schedule.LeaseUntil is { } leaseUntil && leaseUntil > now) return;
        var state = new HealthEvaluationState((EvaluatedHealth)schedule.State, schedule.EvidenceAt,
            (HealthStreak)schedule.Streak, schedule.StreakCount, schedule.StreakAt, schedule.LastEvaluatedAt);
        var result = HealthEvaluation.Expire(state, now, target.IntervalSeconds, target.TimeoutSeconds);
        if (result.ExpiredAt is not { } expired) return;
        var reason = (byte)8;
        if (policy is not null && state.LastObservationAt is { } prior && !SameVersion(schedule.NetworkVersion, policy.Version))
        {
            var boundary = await PolicyBoundaryAsync(db, targetId, policy.Id, policy.Generation, prior, token);
            if (boundary < expired) { expired = boundary; reason = 9; }
        }
        var closed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthStateIntervals] SET [EndedAt] = {expired} WHERE [TargetId] = {targetId} AND [EndedAt] IS NULL AND [StartedAt] <= {expired}", token);
        if (closed != 1) throw new InvalidOperationException("The current health interval is missing or out of order.");
        db.HealthStateIntervals.Add(new HealthStateInterval(targetId, (byte)EvaluatedHealth.Unknown, reason, expired));
        var updated = result.State;
        schedule.SetEvaluation((byte)updated.State, updated.EvidenceAt, (byte)updated.Streak,
            updated.StreakCount, updated.StreakAt, updated.LastObservationAt);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    internal async Task<bool> CommitAsync(HealthWork work, HealthNetworkSnapshot network, HealthObservationKind kind,
        DateTimeOffset observedAt, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await HealthTargetAdministration.LockServiceAsync(db, work.ServiceId, work.InstanceId, true, token) != HealthTargetStatus.Saved) return false;
        var source = db.Sources.Local.SingleOrDefault(x => x.Id == work.SourceId);
        var service = db.Services.Local.SingleOrDefault(x => x.Id == work.ServiceId);
        if (source is null || service is null || service.SourceId != work.SourceId ||
            !source.Version.AsSpan().SequenceEqual(work.SourceVersion) || !service.Version.AsSpan().SequenceEqual(work.ServiceVersion)) return false;
        if (work.InstanceId is { } instanceId)
        {
            var instance = db.Instances.Local.SingleOrDefault(x => x.Id == instanceId);
            if (instance is null || work.InstanceVersion is null || !instance.Version.AsSpan().SequenceEqual(work.InstanceVersion)) return false;
        }
        var target = await db.HealthTargets.FromSqlInterpolated($"SELECT * FROM [service].[HealthTargets] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {work.Lease.TargetId}").SingleOrDefaultAsync(token);
        if (target is null || !target.Enabled || target.ServiceId != work.ServiceId || target.InstanceId != work.InstanceId ||
            !target.Version.AsSpan().SequenceEqual(work.TargetVersion)) return false;
        var policy = await db.HealthNetworkPolicies.FromSqlInterpolated($"SELECT * FROM [service].[HealthNetworkPolicies] WITH (HOLDLOCK) WHERE [Id] = {network.Id} AND [Generation] = {network.Generation}").SingleOrDefaultAsync(token);
        if (policy is null || !policy.Version.AsSpan().SequenceEqual(network.Version)) return false;
        var lease = work.Lease;
        var schedule = await db.HealthSchedules.FromSqlInterpolated($"SELECT * FROM [service].[HealthSchedules] WITH (UPDLOCK, HOLDLOCK) WHERE [TargetId] = {lease.TargetId} AND [LeaseOwner] = {lease.Owner} AND [Generation] = {lease.Generation} AND [Enabled] = 1 AND [LeaseUntil] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')").SingleOrDefaultAsync(token);
        if (schedule is null) return false;
        var receivedAt = await DiscoveryTransaction.NowAsync(db, token);
        if (observedAt > receivedAt) observedAt = receivedAt;
        if (!await ApplyEvaluationAsync(db, schedule, work, network, kind, observedAt, receivedAt, token)) return false;
        db.HealthObservations.Add(new HealthObservation(lease.TargetId, lease.Generation, network.Generation,
            work.SourceVersion, work.ServiceVersion, work.InstanceVersion, work.TargetVersion, network.Version, kind, observedAt, receivedAt));
        await db.SaveChangesAsync(token);
        var completed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [LeaseOwner] = NULL, [LeaseUntil] = NULL, [DueAt] = DATEADD(second, {work.IntervalSeconds}, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [TargetId] = {lease.TargetId} AND [LeaseOwner] = {lease.Owner} AND [Generation] = {lease.Generation} AND [Enabled] = 1 AND [LeaseUntil] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')", token);
        if (completed != 1) return false;
        await transaction.CommitAsync(token);
        return true;
    }

    private static async Task<bool> ApplyEvaluationAsync(ServiceObservabilityDbContext db, HealthSchedule schedule,
        HealthWork work, HealthNetworkSnapshot network, HealthObservationKind outcome,
        DateTimeOffset observedAt, DateTimeOffset receivedAt, CancellationToken token)
    {
        var previous = new HealthEvaluationState((EvaluatedHealth)schedule.State, schedule.EvidenceAt,
            (HealthStreak)schedule.Streak, schedule.StreakCount, schedule.StreakAt, schedule.LastEvaluatedAt);
        var transitions = new List<(byte State, byte Reason, DateTimeOffset At)>(3);
        if (previous.LastObservationAt is { } last &&
            (!SameVersion(schedule.SourceVersion, work.SourceVersion) || !SameVersion(schedule.ServiceVersion, work.ServiceVersion) ||
             !SameVersion(schedule.InstanceVersion, work.InstanceVersion) || !SameVersion(schedule.TargetVersion, work.TargetVersion)))
            throw new InvalidOperationException("Health evidence was not invalidated when its authorization or target changed.");
        if (previous.LastObservationAt is { } prior && !SameVersion(schedule.NetworkVersion, network.Version))
        {
            var boundary = await PolicyBoundaryAsync(db, work.Lease.TargetId, network.Id, network.Generation, prior, token);
            if (boundary > observedAt)
                throw new InvalidOperationException("The health network policy change boundary is missing or out of order.");
            var expiryAtBoundary = HealthEvaluation.Expire(previous, boundary, work.IntervalSeconds, work.TimeoutSeconds);
            if (expiryAtBoundary.ExpiredAt is { } expiredBoundary) transitions.Add(((byte)EvaluatedHealth.Unknown, 8, expiredBoundary));
            else if (previous.State != EvaluatedHealth.Unknown) transitions.Add(((byte)EvaluatedHealth.Unknown, 9, boundary));
            previous = HealthEvaluationState.Initial with { LastObservationAt = prior };
        }
        var result = HealthEvaluation.Apply(previous, outcome, observedAt, work.IntervalSeconds, work.TimeoutSeconds,
            work.FailureThreshold, work.RecoveryThreshold);
        if (result.Ignored) return false;
        if (result.ExpiredAt is { } expired) transitions.Add(((byte)EvaluatedHealth.Unknown, 8, expired));
        if (result.ChangedAtObservation) transitions.Add(((byte)result.State.State, (byte)outcome, observedAt));
        var expiry = HealthEvaluation.Expire(result.State, receivedAt, work.IntervalSeconds, work.TimeoutSeconds);
        if (expiry.ExpiredAt is { } lateExpiry) transitions.Add(((byte)EvaluatedHealth.Unknown, 8, lateExpiry));
        if (transitions.Count != 0)
        {
            var closedAt = transitions[0].At;
            var closed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthStateIntervals] SET [EndedAt] = {closedAt} WHERE [TargetId] = {work.Lease.TargetId} AND [EndedAt] IS NULL AND [StartedAt] <= {closedAt}", token);
            if (closed != 1) throw new InvalidOperationException("The current health interval is missing or out of order.");
            for (var i = 0; i < transitions.Count; i++)
            {
                var transition = transitions[i];
                var interval = new HealthStateInterval(work.Lease.TargetId, transition.State, transition.Reason, transition.At);
                if (i + 1 < transitions.Count) interval.Close(transitions[i + 1].At);
                db.HealthStateIntervals.Add(interval);
            }
        }
        var state = expiry.State;
        schedule.SetEvaluation((byte)state.State, state.EvidenceAt, (byte)state.Streak, state.StreakCount, state.StreakAt, state.LastObservationAt);
        schedule.SetVersions(work.SourceVersion, work.ServiceVersion, work.InstanceVersion, work.TargetVersion, network.Version);
        return true;
    }

    private static bool SameVersion(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    internal static async Task<DateTimeOffset> PolicyBoundaryAsync(ServiceObservabilityDbContext db, Guid targetId,
        Guid policyId, long policyGeneration, DateTimeOffset prior, CancellationToken token)
    {
        var generation = await db.HealthObservations.AsNoTracking().Where(x => x.TargetId == targetId && x.ObservedAt == prior)
            .Select(x => (long?)x.NetworkGeneration).SingleOrDefaultAsync(token);
        var boundary = generation is null ? null : await db.HealthNetworkRevisions.AsNoTracking()
            .Where(x => x.PolicyId == policyId && x.Generation > generation && x.Generation <= policyGeneration)
            .OrderBy(x => x.Generation).Select(x => (DateTimeOffset?)x.OccurredAt).FirstOrDefaultAsync(token);
        if (boundary is null) throw new InvalidOperationException("The health network policy change boundary is missing.");
        // A clock correction cannot move an already recorded interval backwards.
        return boundary < prior ? prior : boundary.Value;
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name; parameter.DbType = type; parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
