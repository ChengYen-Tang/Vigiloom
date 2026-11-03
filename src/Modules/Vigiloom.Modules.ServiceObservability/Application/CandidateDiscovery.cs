using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

/// <summary>Persists only bounded discovery metadata for a currently authorized source.</summary>
public sealed class CandidateDiscovery(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    public const int PerSourceLimit = 1_000;
    public const int GlobalLimit = 10_000;

    public async Task<CandidateObservation> ObserveAsync(Guid sourceId, string serviceKey, string environment, string displayName, CancellationToken token)
    {
        var proposed = new ServiceCandidate(sourceId, serviceKey, environment, displayName, DateTimeOffset.UnixEpoch);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Serialize short capacity transactions across Web instances. Approval and
        // cleanup must take the same lock before touching a source or candidate.
        if (!await DiscoveryTransaction.TryLockAsync(db, transaction, token)) return CandidateObservation.Busy;
        var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null || source.RevokedAt is not null) return CandidateObservation.SourceUnavailable;
        var now = await DiscoveryTransaction.NowAsync(db, token);
        if (await db.Services.AnyAsync(x => x.SourceId == sourceId && x.ServiceKey == proposed.ServiceKey && x.Environment == proposed.Environment, token))
            return CandidateObservation.AlreadyApproved;
        var current = await db.Candidates.SingleOrDefaultAsync(x => x.SourceId == sourceId && x.ServiceKey == proposed.ServiceKey && x.Environment == proposed.Environment, token);
        if (current is null)
        {
            var cutoff = now.AddMinutes(-3);
            await db.Candidates.Where(x => x.LastSeen < cutoff).ExecuteDeleteAsync(token);
            if (await db.Candidates.CountAsync(token) >= GlobalLimit || await db.Candidates.CountAsync(x => x.SourceId == sourceId, token) >= PerSourceLimit)
                return CandidateObservation.CapacityReached;
            db.Candidates.Add(new ServiceCandidate(sourceId, proposed.ServiceKey, proposed.Environment, proposed.DisplayName, now));
        }
        else current.Observe(proposed.DisplayName, now);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return CandidateObservation.Recorded;
    }

    public async Task<IReadOnlyList<CandidateSummary>> ListAsync(int page, CancellationToken token)
    {
        const int pageSize = 50;
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, GlobalLimit / pageSize);
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await DiscoveryTransaction.NowAsync(db, token);
        var cutoff = now.AddMinutes(-3);
        return await db.Candidates.AsNoTracking().Where(x => x.LastSeen >= cutoff && x.LastSeen <= now)
            .Where(x => db.Sources.Any(s => s.Id == x.SourceId && s.RevokedAt == null))
            .OrderByDescending(x => x.LastSeen).ThenByDescending(x => x.RowId)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new CandidateSummary(x.Id, x.SourceId, x.ServiceKey, x.Environment, x.DisplayName, x.FirstSeen, x.LastSeen)).ToArrayAsync(token);
    }
}

public enum CandidateObservation { Recorded, AlreadyApproved, SourceUnavailable, CapacityReached, Busy }
public sealed record CandidateSummary(Guid Id, Guid SourceId, string ServiceKey, string Environment, string DisplayName, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);
