using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Web.Events;

internal sealed class EventTimelineAudit(IDbContextFactory<OperationsDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock, IConfiguration? configuration = null, IServiceProvider? provider = null)
{
    public async Task RecordEvidenceAsync(Guid actor, EventEvidenceAccessed evidence, CancellationToken token)
    {
        var entry = new OperationAudit(actor, evidence.DeviceId ?? evidence.EvidenceId, clock.GetUtcNow().ToUniversalTime(), evidence);
        if (!string.IsNullOrWhiteSpace(configuration?["Services:Operations:Url"]))
        { await provider!.GetRequiredService<RemoteTimelineReader>().RecordAsync(entry, token); return; }
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            entry, token);
        await transaction.CommitAsync(token);
    }

    public async Task RecordAsync(Guid actor, EventTimelineQuery query, int count, bool exported, CancellationToken token)
    {
        var entry = new OperationAudit(actor, actor, clock.GetUtcNow().ToUniversalTime(), new EventTimelineAccessed(query.From.ToUniversalTime(), query.Until.ToUniversalTime(),
            query.GroupId, query.Source, query.Page, count, exported));
        if (!string.IsNullOrWhiteSpace(configuration?["Services:Operations:Url"]))
        { await provider!.GetRequiredService<RemoteTimelineReader>().RecordAsync(entry, token); return; }
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            entry, token);
        await transaction.CommitAsync(token);
    }
}
