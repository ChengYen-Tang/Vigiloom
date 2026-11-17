using Microsoft.EntityFrameworkCore;

namespace Vigiloom.Infrastructure.Persistence;

/// <summary>Counts and deletes a bounded batch of time-stamped module audit records.</summary>
public static class AuditRetentionQueries
{
    public static Task<long> CountAsync<T>(DbSet<T> records, DateTimeOffset cutoff, CancellationToken token) where T : class =>
        records.LongCountAsync(x => EF.Property<DateTimeOffset>(x, "OccurredAt") < cutoff, token);

    public static Task<int> DeleteBatchAsync<T>(DbSet<T> records, DateTimeOffset cutoff, int limit, CancellationToken token) where T : class
    {
        var ids = records.Where(x => EF.Property<DateTimeOffset>(x, "OccurredAt") < cutoff)
            .OrderBy(x => EF.Property<DateTimeOffset>(x, "OccurredAt")).ThenBy(x => EF.Property<long>(x, "RowId"))
            .Select(x => EF.Property<long>(x, "RowId")).Take(limit);
        return records.Where(x => ids.Contains(EF.Property<long>(x, "RowId"))).ExecuteDeleteAsync(token);
    }
}
