using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

/// <summary>Returns one Agent-owned scope's durable progress without exposing its contents.</summary>
public sealed class IntegrityStatusReader : IIntegrityStatusReader
{
    public async Task<IntegrityScopeStatus> ReadAsync(DbConnection connection, DbTransaction transaction, IntegrityOwner owner,
        Guid scopeId, CancellationToken token)
    {
        if (scopeId.Version != 7) throw new ArgumentException("A version 7 scope ID is required.", nameof(scopeId));
        if (!await AgentSnapshotLock.TryAcquireAsync(connection, transaction, "Integrity", owner.AgentId, 5000, token))
            throw new TimeoutException("Integrity status is temporarily busy.");
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var setting = await db.IntegrityScopes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scopeId && x.DeviceId == owner.DeviceId, token);
        var present = setting is not null && setting.RetiredAt is null;
        var enabled = present ? IntegrityConfiguration.View(setting!).Scope.Enabled : (bool?)null;
        var state = await db.IntegrityScopeStates.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == owner.AgentId &&
            x.DeviceId == owner.DeviceId && x.DeviceGeneration == owner.DeviceGeneration && x.ScopeId == scopeId, token);
        var otherActive = await db.IntegrityScopeStates.AsNoTracking().AnyAsync(x => x.AgentId == owner.AgentId &&
            x.DeviceId == owner.DeviceId && x.DeviceGeneration == owner.DeviceGeneration && x.ScopeId != scopeId && x.ActiveGeneration != null, token);
        return state is null ? new(scopeId, present, present ? setting!.Revision : null, enabled, 0, null, otherActive, null, null, null, null, null, null) :
            new(scopeId, present, present ? setting!.Revision : null, enabled, Math.Max(state.ClosedGeneration, state.ActiveGeneration ?? 0), state.ActiveGeneration, otherActive,
                state.LastAttemptGeneration, state.LastOutcome, state.LastStatus, state.LastSnapshotId, state.LastPartialReason, state.LastCoverageGap);
    }
}
