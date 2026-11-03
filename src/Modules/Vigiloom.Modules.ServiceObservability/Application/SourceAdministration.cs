using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

/// <summary>Manages source credentials and writes an audit event in the same transaction.</summary>
public sealed class SourceAdministration(IDbContextFactory<ServiceObservabilityDbContext> contexts, IOperationAuditWriter<OperationAudit> audit)
{
    public async Task<SourceCredentialIssue> IssueAsync(string name, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("An administrator is required.", nameof(actorId));
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var now = await DiscoveryTransaction.NowAsync(db, token);
        var (source, credential) = SourceCredentials.Create(name, now);
        db.Sources.Add(source);
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actorId, source.Id, now, new ServiceSourceChanged(ServiceSourceAction.Issued)), token);
        await transaction.CommitAsync(token);
        return new(source.Id, credential);
    }

    public async Task<bool> RevokeAsync(Guid sourceId, Guid actorId, CancellationToken token)
    {
        if (sourceId.Version != 7 || actorId == Guid.Empty) throw new ArgumentException("A source and administrator are required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null) return false;
        if (source.RevokedAt is not null) return true;
        var now = await DiscoveryTransaction.NowAsync(db, token);
        source.Revoke(now);
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actorId, source.Id, now, new ServiceSourceChanged(ServiceSourceAction.Revoked)), token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<IReadOnlyList<ServiceSourceSummary>> ListAsync(int page, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 200);
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Sources.AsNoTracking().OrderByDescending(x => x.RowId).Skip((page - 1) * 50).Take(50)
            .Select(x => new ServiceSourceSummary(x.Id, x.Name, x.CreatedAt, x.RevokedAt)).ToArrayAsync(token);
    }
}
