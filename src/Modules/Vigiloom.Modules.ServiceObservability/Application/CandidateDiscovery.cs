using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using System.Text;

namespace Vigiloom.Modules.ServiceObservability.Application;

/// <summary>Persists only bounded discovery metadata for a currently authorized source.</summary>
public sealed class CandidateDiscovery(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    public const int PerSourceLimit = 1_000;
    public const int GlobalLimit = 10_000;

    public Task<CandidateObservation> ObserveAsync(AuthenticatedSource source, string serviceKey, string environment, string displayName, CancellationToken token, string? serviceNamespace = null) =>
        source.Version is { Length: 8 } ? ObserveCoreAsync(source.SourceId, source.Version, serviceKey, environment, displayName, token, serviceNamespace) : Task.FromResult(CandidateObservation.SourceUnavailable);

    internal Task<CandidateObservation> ObserveAsync(Guid sourceId, string serviceKey, string environment, string displayName, CancellationToken token, string? serviceNamespace = null) =>
        ObserveCoreAsync(sourceId, null, serviceKey, environment, displayName, token, serviceNamespace);

    private async Task<CandidateObservation> ObserveCoreAsync(Guid sourceId, byte[]? version, string serviceKey, string environment, string displayName, CancellationToken token, string? serviceNamespace)
    {
        var proposed = new ServiceCandidate(sourceId, serviceKey, environment, displayName, DateTimeOffset.UnixEpoch, serviceNamespace);
        var namespaceBytes = Encoding.Unicode.GetBytes(proposed.ServiceNamespace);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Serialize short capacity transactions across Web instances. Approval and
        // cleanup must take the same lock before touching a source or candidate.
        if (!await DiscoveryTransaction.TryLockAsync(db, transaction, token)) return CandidateObservation.Busy;
        var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null || source.RevokedAt is not null || (version is not null && !source.Version.AsSpan().SequenceEqual(version))) return CandidateObservation.SourceUnavailable;
        var now = await DiscoveryTransaction.NowAsync(db, token);
        if (await db.Services.AnyAsync(x => x.SourceId == sourceId && x.ServiceNamespaceBytes == namespaceBytes && x.ServiceKey == proposed.ServiceKey && x.Environment == proposed.Environment, token))
            return CandidateObservation.AlreadyApproved;
        var current = await db.Candidates.SingleOrDefaultAsync(x => x.SourceId == sourceId && x.ServiceNamespaceBytes == namespaceBytes && x.ServiceKey == proposed.ServiceKey && x.Environment == proposed.Environment, token);
        if (current is null)
        {
            var cutoff = now.AddMinutes(-3);
            await db.Candidates.Where(x => x.LastSeen < cutoff).ExecuteDeleteAsync(token);
            if (await db.Candidates.CountAsync(token) >= GlobalLimit || await db.Candidates.CountAsync(x => x.SourceId == sourceId, token) >= PerSourceLimit)
                return CandidateObservation.CapacityReached;
            db.Candidates.Add(new ServiceCandidate(sourceId, proposed.ServiceKey, proposed.Environment, proposed.DisplayName, now, proposed.ServiceNamespace));
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
        return await (from candidate in db.Candidates.AsNoTracking()
                      join source in db.Sources.AsNoTracking() on candidate.SourceId equals source.Id
                      where candidate.LastSeen >= cutoff && candidate.LastSeen <= now && source.RevokedAt == null
                      orderby candidate.LastSeen descending, candidate.RowId descending
                      select new { candidate, source.Name })
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new CandidateSummary(x.candidate.Id, x.candidate.SourceId, x.candidate.ServiceKey, x.candidate.Environment, x.candidate.DisplayName, x.candidate.FirstSeen, x.candidate.LastSeen, x.Name, x.candidate.ServiceNamespace)).ToArrayAsync(token);
    }
}

public enum CandidateObservation { Recorded, AlreadyApproved, SourceUnavailable, CapacityReached, Busy }
