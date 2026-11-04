using System.Data;
using System.Data.Common;

namespace Vigiloom.Infrastructure.LogAdmission;

/// <summary>A count for an authenticated source and a service authorized in the same transaction.</summary>
public readonly record struct TraceSource(Guid SourceId, Guid ServiceId, int Count);

/// <summary>Atomically admits trace spans against separate central and per-service-source budgets.</summary>
public static class TraceAdmission
{
    /// <remarks>The caller must authenticate and revalidate each service in this transaction. Call once per transaction with the entire batch; keep the transaction open through the writes and commit.</remarks>
    public static async Task<LogAdmissionResult> AdmitAsync(DbConnection connection, DbTransaction transaction,
        IReadOnlyCollection<TraceSource> sources, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(sources);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("An open connection and its active transaction are required.");
        if (sources.Count == 0 || sources.Count > 256 || sources.Any(x => x.SourceId.Version != 7 || x.ServiceId.Version != 7 || x.Count <= 0))
            throw new ArgumentException("Authenticated service sources and positive counts are required.", nameof(sources));
        var groups = sources.GroupBy(x => (x.SourceId, x.ServiceId)).Select(x => (x.Key.SourceId, x.Key.ServiceId, Count: x.Sum(y => (long)y.Count)))
            .OrderBy(x => x.SourceId).ThenBy(x => x.ServiceId).ToArray();
        var total = groups.Sum(x => x.Count);
        var policy = await ReadPolicyAsync(connection, transaction, token);
        if (total > policy.GlobalBurst || groups.Any(x => x.Count > policy.SourceBurst)) return new(LogAdmissionStatus.BatchTooLarge);
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'Vigiloom.TraceAdmission', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
            if (Convert.ToInt32(await guard.ExecuteScalarAsync(token)) < 0) return new(LogAdmissionStatus.Busy);
        }
        var now = await NowAsync(connection, transaction, token);
        var central = TokenBucket.Refill(await ReadAsync(connection, transaction, Guid.Empty, Guid.Empty, token),
            policy.GlobalBurst, policy.GlobalRate, policy.Version, policy.UpdatedAt, now);
        var leaves = new List<(Guid Source, Guid Service, long Needed, TokenBucket.State Bucket)>(groups.Length);
        var needed = total * TokenBucket.Unit;
        var wait = TokenBucket.RetryMicroseconds(needed - central.Tokens, policy.GlobalRate, central.LastRefill, now);
        foreach (var group in groups)
        {
            var leaf = TokenBucket.Refill(await ReadAsync(connection, transaction, group.SourceId, group.ServiceId, token),
                policy.SourceBurst, policy.SourceRate, policy.Version, policy.UpdatedAt, now);
            var leafNeeded = group.Count * TokenBucket.Unit;
            wait = Math.Max(wait, TokenBucket.RetryMicroseconds(leafNeeded - leaf.Tokens, policy.SourceRate, leaf.LastRefill, now));
            leaves.Add((group.SourceId, group.ServiceId, leafNeeded, leaf));
        }
        if (wait > 0) return new(LogAdmissionStatus.RateLimited, TimeSpan.FromTicks(wait * 10));
        await SaveAsync(connection, transaction, Guid.Empty, Guid.Empty, central with { Tokens = central.Tokens - needed }, token);
        foreach (var leaf in leaves)
            await SaveAsync(connection, transaction, leaf.Source, leaf.Service, leaf.Bucket with { Tokens = leaf.Bucket.Tokens - leaf.Needed }, token);
        return new(LogAdmissionStatus.Accepted);
    }

    private static async Task<Policy> ReadPolicyAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT [SourceRate],[SourceBurst],[GlobalRate],[GlobalBurst],[Version],[UpdatedAt] FROM [logquota].[TracePolicies] WITH (HOLDLOCK) WHERE [Name] = N'central'";
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<DateTimeOffset>(5))
            : new(200, 1_000, 2_000, 4_000, [], DateTimeOffset.MinValue);
    }

    private static async Task<DateTimeOffset> NowAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')";
        return (DateTimeOffset)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<TokenBucket.State?> ReadAsync(DbConnection connection, DbTransaction transaction, Guid source, Guid service, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT [Microtokens],[LastRefill],[PolicyVersion] FROM [logquota].[TraceBuckets] WHERE [SourceId] = @source AND [ServiceId] = @service";
        Add(command, "@source", DbType.Guid, source); Add(command, "@service", DbType.Guid, service);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<byte[]>(2), true) : null;
    }

    private static async Task SaveAsync(DbConnection connection, DbTransaction transaction, Guid source, Guid service, TokenBucket.State bucket, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = bucket.Exists
            ? "UPDATE [logquota].[TraceBuckets] SET [Microtokens]=@tokens,[LastRefill]=@last,[PolicyVersion]=@version WHERE [SourceId]=@source AND [ServiceId]=@service"
            : "INSERT INTO [logquota].[TraceBuckets] ([SourceId],[ServiceId],[Microtokens],[LastRefill],[PolicyVersion]) VALUES (@source,@service,@tokens,@last,@version)";
        Add(command, "@source", DbType.Guid, source); Add(command, "@service", DbType.Guid, service);
        Add(command, "@tokens", DbType.Int64, bucket.Tokens); Add(command, "@last", DbType.DateTimeOffset, bucket.LastRefill);
        Add(command, "@version", DbType.Binary, bucket.PolicyVersion, 8);
        await command.ExecuteNonQueryAsync(token);
    }

    private static void Add(DbCommand command, string name, DbType type, object value, int size = 0)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.DbType = type; parameter.Value = value;
        if (size != 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }

    private sealed record Policy(int SourceRate, int SourceBurst, int GlobalRate, int GlobalBurst, byte[] Version, DateTimeOffset UpdatedAt);
}
