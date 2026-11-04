using System.Data;
using System.Data.Common;

namespace Vigiloom.Infrastructure.LogAdmission;

public enum LogSourceKind : byte { Agent = 1, Application = 2 }
public readonly record struct LogSource(LogSourceKind Kind, Guid Id);
public enum LogAdmissionStatus { Accepted, RateLimited, BatchTooLarge, Busy }
public sealed record LogAdmissionResult(LogAdmissionStatus Status, TimeSpan? RetryAfter = null);

/// <summary>Atomically admits a count of logs into the shared central and authenticated source budgets.</summary>
public static class LogAdmission
{

    /// <remarks>
    /// The source must be derived from authentication, and the caller must revalidate authorization
    /// and deduplication before this call. Keep the same transaction through data persistence and
    /// commit. Call once per transaction, for one source. Never acknowledge an uncommitted write.
    /// </remarks>
    public static async Task<LogAdmissionResult> AdmitAsync(DbConnection connection, DbTransaction transaction,
        LogSource source, int count, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("An open connection and its active transaction are required.");
        if (source.Kind is not (LogSourceKind.Agent or LogSourceKind.Application) || source.Id.Version != 7 || count <= 0)
            throw new ArgumentException("A trusted source and positive record count are required.");

        var policy = await ReadPolicyAsync(connection, transaction, token);
        if (count > policy.SourceBurst || count > policy.GlobalBurst) return new(LogAdmissionStatus.BatchTooLarge);
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'Vigiloom.LogAdmission', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
            if (Convert.ToInt32(await guard.ExecuteScalarAsync(token)) < 0) return new(LogAdmissionStatus.Busy);
        }
        var now = await ReadNowAsync(connection, transaction, token);
        var global = await ReadBucketAsync(connection, transaction, 0, Guid.Empty, token);
        var individual = await ReadBucketAsync(connection, transaction, (byte)source.Kind, source.Id, token);
        global = TokenBucket.Refill(global, policy.GlobalBurst, policy.GlobalRate, policy.Version, policy.UpdatedAt, now);
        individual = TokenBucket.Refill(individual, policy.SourceBurst, policy.SourceRate, policy.Version, policy.UpdatedAt, now);
        var needed = count * TokenBucket.Unit;
        if (global.Tokens < needed || individual.Tokens < needed)
        {
            var globalWait = TokenBucket.RetryMicroseconds(needed - global.Tokens, policy.GlobalRate, global.LastRefill, now);
            var sourceWait = TokenBucket.RetryMicroseconds(needed - individual.Tokens, policy.SourceRate, individual.LastRefill, now);
            return new(LogAdmissionStatus.RateLimited, TimeSpan.FromTicks(Math.Max(globalWait, sourceWait) * 10));
        }
        await SaveBucketAsync(connection, transaction, 0, Guid.Empty, global with { Tokens = global.Tokens - needed }, token);
        await SaveBucketAsync(connection, transaction, (byte)source.Kind, source.Id, individual with { Tokens = individual.Tokens - needed }, token);
        return new(LogAdmissionStatus.Accepted);
    }

    private static async Task<Policy> ReadPolicyAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT [SourceRate], [SourceBurst], [GlobalRate], [GlobalBurst], [Version], [UpdatedAt] FROM [logquota].[Policies] WITH (HOLDLOCK) WHERE [Name] = N'central'";
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<DateTimeOffset>(5))
            : new(20, 100, 1_000, 2_000, [], DateTimeOffset.MinValue);
    }

    private static async Task<DateTimeOffset> ReadNowAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')";
        return (DateTimeOffset)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<TokenBucket.State?> ReadBucketAsync(DbConnection connection, DbTransaction transaction, byte kind, Guid source, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT [Microtokens], [LastRefill], [PolicyVersion] FROM [logquota].[Buckets] WHERE [Kind] = @kind AND [SourceId] = @source";
        Add(command, "@kind", DbType.Byte, kind); Add(command, "@source", DbType.Guid, source);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<byte[]>(2), true) : null;
    }

    private static async Task SaveBucketAsync(DbConnection connection, DbTransaction transaction, byte kind, Guid source, TokenBucket.State bucket, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = bucket.Exists
            ? "UPDATE [logquota].[Buckets] SET [Microtokens] = @tokens, [LastRefill] = @last, [PolicyVersion] = @version WHERE [Kind] = @kind AND [SourceId] = @source"
            : "INSERT INTO [logquota].[Buckets] ([Kind], [SourceId], [Microtokens], [LastRefill], [PolicyVersion]) VALUES (@kind, @source, @tokens, @last, @version)";
        Add(command, "@kind", DbType.Byte, kind); Add(command, "@source", DbType.Guid, source);
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
