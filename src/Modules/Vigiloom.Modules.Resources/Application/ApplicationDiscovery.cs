using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Records service identity only after the telemetry receiver authenticates its source.</summary>
public sealed class ApplicationDiscovery(IDbContextFactory<ResourcesDbContext> contexts)
{
    public async Task<ApplicationObservationStatus> ObserveAsync(ApplicationObservation observation, CancellationToken token)
    {
        var proposed = new ApplicationCandidate(observation.SourceId, observation.ServiceKey, observation.Environment,
            observation.DisplayName, observation.ServiceNamespace, DateTimeOffset.UnixEpoch);
        var namespaceBytes = Encoding.Unicode.GetBytes(proposed.ServiceNamespace);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await LockAsync(db, transaction, token);
        if (await db.ApplicationServices.AnyAsync(s => s.SourceId == proposed.SourceId && s.ServiceNamespaceBytes == namespaceBytes &&
            s.ServiceKey == proposed.ServiceKey && s.Environment == proposed.Environment, token))
            return ApplicationObservationStatus.AlreadyApproved;
        var now = await DatabaseTimeAsync(db, token);
        var candidate = await db.ApplicationCandidates.SingleOrDefaultAsync(c => c.SourceId == proposed.SourceId && c.ServiceNamespaceBytes == namespaceBytes &&
            c.ServiceKey == proposed.ServiceKey && c.Environment == proposed.Environment, token);
        if (candidate is null)
        {
            var cutoff = now.AddMinutes(-3);
            await db.ApplicationCandidates.Where(c => c.LastSeen < cutoff).ExecuteDeleteAsync(token);
            db.ApplicationCandidates.Add(new ApplicationCandidate(proposed.SourceId, proposed.ServiceKey, proposed.Environment, proposed.DisplayName, proposed.ServiceNamespace, now));
        }
        else candidate.Observe(proposed.DisplayName, now);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ApplicationObservationStatus.Recorded;
    }

    public async Task<IReadOnlyList<ApplicationCandidateInfo>> ListAsync(int page, CancellationToken token)
    {
        page = Math.Clamp(page, 1, int.MaxValue / 50);
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await DatabaseTimeAsync(db, token);
        var cutoff = now.AddMinutes(-3);
        return await db.ApplicationCandidates.AsNoTracking().Where(c => c.LastSeen >= cutoff && c.LastSeen <= now)
            .OrderByDescending(c => c.LastSeen).ThenByDescending(c => c.RowId).Skip((page - 1) * 50).Take(50)
            .Select(c => new ApplicationCandidateInfo(c.Id, c.SourceId, c.ServiceKey, c.Environment, c.DisplayName, c.ServiceNamespace, c.FirstSeen, c.LastSeen))
            .ToArrayAsync(token);
    }

    internal static Task<DateTimeOffset> DatabaseTimeAsync(ResourcesDbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);

    internal static async Task LockAsync(ResourcesDbContext db, IDbContextTransaction transaction, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'Vigiloom.ResourceCandidates', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0) throw new InvalidOperationException("Could not acquire the candidate lock.");
    }
}
