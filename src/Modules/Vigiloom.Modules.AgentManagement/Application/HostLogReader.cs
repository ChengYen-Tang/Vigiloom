using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Supplies retained source events to other modules without exposing Agent storage keys.</summary>
public sealed class HostLogReader(IDbContextFactory<AgentManagementDbContext> contexts) : IHostLogReader
{
    public async Task<IReadOnlyList<HostLogEntry>> ReadAsync(Guid agentId, IReadOnlyCollection<Guid> messageIds, CancellationToken token)
    {
        if (agentId.Version != 7 || messageIds.Count > AgentLogBatchCodec.MaximumItems)
            throw new ArgumentException("Invalid Agent or event batch.");
        if (messageIds.Count == 0) return [];

        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.HostLogs.AsNoTracking().Where(x => x.AgentId == agentId && messageIds.Contains(x.MessageId))
            .Select(x => new { x.AgentId, x.MessageId, x.SourceId, x.SourceKind, x.SourceName, x.ParserVersion,
                x.OccurredAt, x.ReceivedAt, x.Content, x.Provider, x.EventId, x.Level, x.User, x.Process, x.GapReason, x.GapAffectedCount })
            .ToArrayAsync(token);
        return rows.Select(x => new HostLogEntry(x.AgentId, x.MessageId, x.SourceId, x.SourceKind, x.SourceName, x.ParserVersion,
            x.OccurredAt, x.ReceivedAt, x.Content, x.Provider, x.EventId, x.Level, x.User, x.Process,
            x.GapReason is { } reason ? new(reason, x.GapAffectedCount) : null)).ToArray();
    }
}
