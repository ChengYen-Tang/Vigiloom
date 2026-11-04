using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Infrastructure.LogAdmission.Persistence;

namespace Vigiloom.Infrastructure.LogAdmission;

public sealed record LogQuotaValues(int SourceRate, int SourceBurst, int GlobalRate, int GlobalBurst)
{
    public bool IsValid => SourceRate > 0 && SourceBurst > 0 && GlobalRate > 0 && GlobalBurst > 0;
    public static LogQuotaValues Default { get; } = new(20, 100, 1_000, 2_000);
    public static LogQuotaValues TraceDefault { get; } = new(200, 1_000, 2_000, 4_000);
}
public sealed record LogQuotaSnapshot(LogQuotaValues Values, string Version);
public sealed record LogQuotaChange(Guid PolicyId, Guid ActorId, DateTimeOffset OccurredAt, LogQuotaSnapshot Previous, LogQuotaSnapshot Current);

/// <summary>Persists a versioned Log policy and its host-supplied transactional audit.</summary>
public sealed class PolicyAdministration(IDbContextFactory<LogAdmissionDbContext> contexts)
{
    public Task<LogQuotaSnapshot> GetAsync(CancellationToken token) => QuotaPolicyStore.GetAsync<LogAdmissionPolicy>(contexts, LogQuotaValues.Default, token);
    public Task<LogQuotaSnapshot?> UpdateAsync(LogQuotaValues values, string version, Guid actor,
        Func<DbConnection, DbTransaction, LogQuotaChange, CancellationToken, Task> audit, CancellationToken token) =>
        QuotaPolicyStore.UpdateAsync<LogAdmissionPolicy>(contexts, values, version, actor, audit, token);
}

/// <summary>Persists a versioned Trace policy and its host-supplied transactional audit.</summary>
public sealed class TracePolicyAdministration(IDbContextFactory<LogAdmissionDbContext> contexts)
{
    public Task<LogQuotaSnapshot> GetAsync(CancellationToken token) => QuotaPolicyStore.GetAsync<TraceAdmissionPolicy>(contexts, LogQuotaValues.TraceDefault, token);
    public Task<LogQuotaSnapshot?> UpdateAsync(LogQuotaValues values, string version, Guid actor,
        Func<DbConnection, DbTransaction, LogQuotaChange, CancellationToken, Task> audit, CancellationToken token) =>
        QuotaPolicyStore.UpdateAsync<TraceAdmissionPolicy>(contexts, values, version, actor, audit, token);
}

internal static class QuotaPolicyStore
{
    internal static async Task<LogQuotaSnapshot> GetAsync<T>(IDbContextFactory<LogAdmissionDbContext> contexts, LogQuotaValues defaults, CancellationToken token)
        where T : class, IAdmissionPolicy, new()
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var policy = await db.Set<T>().AsNoTracking().SingleOrDefaultAsync(x => x.Name == "central", token);
        return policy is null ? new(defaults, "") : Snapshot(policy);
    }

    internal static async Task<LogQuotaSnapshot?> UpdateAsync<T>(IDbContextFactory<LogAdmissionDbContext> contexts,
        LogQuotaValues values, string version, Guid actor, Func<DbConnection, DbTransaction, LogQuotaChange, CancellationToken, Task> audit, CancellationToken token)
        where T : class, IAdmissionPolicy, new()
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(audit);
        if (!values.IsValid || actor == Guid.Empty) throw new ArgumentException("Positive limits and an administrator are required.");
        byte[] originalVersion;
        try { originalVersion = Convert.FromBase64String(version ?? ""); }
        catch (FormatException) { return null; }
        if (originalVersion.Length is not (0 or 8)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (typeof(T) == typeof(LogAdmissionPolicy))
            await db.Database.ExecuteSqlRawAsync("SELECT [Id] FROM [logquota].[Policies] WITH (XLOCK, HOLDLOCK) WHERE [Name] = N'central'", token);
        else if (typeof(T) == typeof(TraceAdmissionPolicy))
            await db.Database.ExecuteSqlRawAsync("SELECT [Id] FROM [logquota].[TracePolicies] WITH (XLOCK, HOLDLOCK) WHERE [Name] = N'central'", token);
        else throw new InvalidOperationException("Unsupported quota policy.");
        var policy = await db.Set<T>().SingleOrDefaultAsync(x => x.Name == "central", token);
        if (policy is null)
        {
            if (originalVersion.Length != 0) return null;
            policy = new T(); db.Set<T>().Add(policy);
        }
        else
        {
            if (originalVersion.Length != 8) return null;
            db.Entry(policy).Property(x => x.Version).OriginalValue = originalVersion;
        }
        var previous = Snapshot(policy);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        if (policy.CreatedAt == default) policy.CreatedAt = now;
        policy.SourceRate = values.SourceRate; policy.SourceBurst = values.SourceBurst;
        policy.GlobalRate = values.GlobalRate; policy.GlobalBurst = values.GlobalBurst;
        policy.UpdatedAt = now; policy.UpdatedBy = actor;
        db.Entry(policy).Property(x => x.UpdatedAt).IsModified = true;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
        var current = Snapshot(policy);
        await audit(db.Database.GetDbConnection(), transaction.GetDbTransaction(), new(policy.Id, actor, now, previous, current), token);
        await transaction.CommitAsync(token);
        return current;
    }

    private static LogQuotaSnapshot Snapshot(IAdmissionPolicy policy) => new(new(policy.SourceRate, policy.SourceBurst, policy.GlobalRate, policy.GlobalBurst), Convert.ToBase64String(policy.Version));
}
