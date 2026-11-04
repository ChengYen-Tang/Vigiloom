using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Manages the central destination policy and immutable revisions.</summary>
public sealed class HealthNetworkPolicyAdministration(IDbContextFactory<ServiceObservabilityDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit)
{
    public async Task<HealthNetworkPolicySnapshot> GetAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var policy = await db.HealthNetworkPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.Name == "central", token);
        return policy is null ? new([], 0, "") : Map(policy);
    }

    public async Task<IReadOnlyList<HealthNetworkRevisionSnapshot>> ListRevisionsAsync(int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.HealthNetworkRevisions.AsNoTracking().OrderByDescending(x => x.Generation)
            .Skip((page - 1) * 50).Take(50).ToArrayAsync(token);
        return rows.Select(x => new HealthNetworkRevisionSnapshot(x.Generation, x.OccurredAt, x.ActorId,
            HealthNetworkPolicy.ParseNetworks(x.NetworksJson))).ToArray();
    }

    public async Task<HealthNetworkPolicySnapshot?> UpdateAsync(HealthNetworkPolicyInput input, Guid actor, CancellationToken token)
    {
        if (input is null || input.Networks is null || actor == Guid.Empty) throw new ArgumentException("Networks and an administrator are required.");
        byte[] version;
        try { version = Convert.FromBase64String(input.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;

        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var policy = await db.HealthNetworkPolicies.FromSqlRaw("SELECT * FROM [service].[HealthNetworkPolicies] WITH (XLOCK, HOLDLOCK) WHERE [Name] = 'central'").SingleOrDefaultAsync(token);
        var previousGeneration = policy?.Generation ?? 0;
        var previousVersion = policy is null ? "" : Convert.ToBase64String(policy.Version);
        var now = await DiscoveryTransaction.NowAsync(db, token);
        if (policy is null)
        {
            if (version.Length != 0) return null;
            policy = new HealthNetworkPolicy(input.Networks, now);
            db.HealthNetworkPolicies.Add(policy);
        }
        else
        {
            if (version.Length != 8) return null;
            db.Entry(policy).Property(x => x.Version).OriginalValue = version;
            policy.ReplaceNetworks(input.Networks, now);
        }
        db.HealthNetworkRevisions.Add(new HealthNetworkRevision(policy, actor, now));
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, policy.Id, now, new HealthNetworkPolicyChanged(previousGeneration, policy.Generation,
                previousVersion, Convert.ToBase64String(policy.Version))), token);
        await transaction.CommitAsync(token);
        return Map(policy);
    }

    private static HealthNetworkPolicySnapshot Map(HealthNetworkPolicy policy) =>
        new(HealthNetworkPolicy.ParseNetworks(policy.NetworksJson),
            policy.Generation, Convert.ToBase64String(policy.Version));
}
