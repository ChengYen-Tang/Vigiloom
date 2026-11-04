using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

public sealed class InstancePolicyAdministration(IDbContextFactory<ServiceObservabilityDbContext> contexts,
    TimeProvider clock, IOperationAuditWriter<OperationAudit> audit)
{
    public async Task<InstancePolicySnapshot> GetAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var policy = await db.InstancePolicies.AsNoTracking().SingleOrDefaultAsync(x => x.Name == "central", token);
        return policy is null ? new(1_000, 100_000, "") : Map(policy);
    }

    /// <returns>The updated setting, or null when its version has changed.</returns>
    public async Task<InstancePolicySnapshot?> UpdateAsync(InstancePolicyInput input, Guid actor, CancellationToken token)
    {
        if (input.PerService <= 0 || input.Total <= 0 || actor == Guid.Empty) throw new ArgumentException("Positive limits and an administrator are required.");
        byte[] version;
        try { version = Convert.FromBase64String(input.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var policy = await db.InstancePolicies.SingleOrDefaultAsync(x => x.Name == "central", token);
        if (policy is null)
        {
            if (version.Length != 0) return null;
            policy = new InstancePolicy { CreatedAt = clock.GetUtcNow() };
            db.InstancePolicies.Add(policy);
        }
        else
        {
            if (version.Length != 8) return null;
            db.Entry(policy).Property(x => x.Version).OriginalValue = version;
        }
        var previous = new InstancePolicyValues(policy.PerService, policy.Total);
        var previousVersion = Convert.ToBase64String(policy.Version);
        policy.PerService = input.PerService;
        policy.Total = input.Total;
        policy.UpdatedAt = clock.GetUtcNow().ToUniversalTime();
        policy.UpdatedBy = actor;
        db.Entry(policy).Property(x => x.UpdatedAt).IsModified = true;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, policy.Id, policy.UpdatedAt, new InstancePolicyChanged(previous,
                new(policy.PerService, policy.Total), previousVersion, Convert.ToBase64String(policy.Version))), token);
        await transaction.CommitAsync(token);
        return Map(policy);
    }

    private static InstancePolicySnapshot Map(InstancePolicy policy) => new(policy.PerService, policy.Total, Convert.ToBase64String(policy.Version));
}
