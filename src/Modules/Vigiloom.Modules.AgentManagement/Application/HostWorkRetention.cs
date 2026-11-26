using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Removes terminal Host outbox history using the platform retention setting.</summary>
public sealed class HostWorkRetention(IDbContextFactory<HostMonitoringDbContext> contexts, IHostWorkRetentionDays settings)
{
    public async Task<int> RunAsync(CancellationToken token)
    {
        var days = await settings.GetAsync(token);
        if (days is < 1 or > 3650) throw new InvalidOperationException("Invalid background-work retention setting.");
        var total = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            var cutoff = now.AddDays(-days);
            var removed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                ;WITH expired AS
                (
                    SELECT TOP (500) * FROM scheduling.Outbox
                    WHERE (State = 2 AND CompletedAt < {cutoff}) OR
                          (State = 3 AND COALESCE(LastAttemptAt, CreatedAt) < {cutoff})
                    ORDER BY COALESCE(CompletedAt, LastAttemptAt, CreatedAt), RowId
                )
                DELETE FROM expired;
                """, token);
            total = checked(total + removed);
            if (removed < 500) return total;
        }
    }
}
