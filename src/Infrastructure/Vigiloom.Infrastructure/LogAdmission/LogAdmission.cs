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
        var locked = await LockAndReadBucketsAsync(connection, transaction, source, token);
        if (locked is null) return new(LogAdmissionStatus.Busy);
        var (now, global, individual) = locked.Value;
        global = TokenBucket.Refill(global, policy.GlobalBurst, policy.GlobalRate, policy.Version, policy.UpdatedAt, now);
        individual = TokenBucket.Refill(individual, policy.SourceBurst, policy.SourceRate, policy.Version, policy.UpdatedAt, now);
        var needed = count * TokenBucket.Unit;
        if (global.Tokens < needed || individual.Tokens < needed)
        {
            var globalWait = TokenBucket.RetryMicroseconds(needed - global.Tokens, policy.GlobalRate, global.LastRefill, now);
            var sourceWait = TokenBucket.RetryMicroseconds(needed - individual.Tokens, policy.SourceRate, individual.LastRefill, now);
            return new(LogAdmissionStatus.RateLimited, TimeSpan.FromTicks(Math.Max(globalWait, sourceWait) * 10));
        }
        await SaveBucketsAsync(connection, transaction, source, global with { Tokens = global.Tokens - needed },
            individual with { Tokens = individual.Tokens - needed }, token);
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

    private static async Task<(DateTimeOffset Now, TokenBucket.State? Global, TokenBucket.State? Individual)?> LockAndReadBucketsAsync(
        DbConnection connection, DbTransaction transaction, LogSource source, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SET NOCOUNT ON;
            DECLARE @r int;
            EXEC @r = sys.sp_getapplock @Resource = N'Vigiloom.LogAdmission', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
            SELECT @r;
            IF @r >= 0
            BEGIN
                SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
                SELECT [Kind], [Microtokens], [LastRefill], [PolicyVersion] FROM [logquota].[Buckets]
                WHERE ([Kind] = 0 AND [SourceId] = @global) OR ([Kind] = @kind AND [SourceId] = @source);
            END
            """;
        Add(command, "@global", DbType.Guid, Guid.Empty);
        Add(command, "@kind", DbType.Byte, (byte)source.Kind); Add(command, "@source", DbType.Guid, source.Id);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        if (reader.GetInt32(0) < 0) return null;
        await reader.NextResultAsync(token);
        await reader.ReadAsync(token);
        var now = reader.GetFieldValue<DateTimeOffset>(0);
        await reader.NextResultAsync(token);
        TokenBucket.State? global = null, individual = null;
        while (await reader.ReadAsync(token))
        {
            var bucket = new TokenBucket.State(reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<byte[]>(3), true);
            if (reader.GetByte(0) == 0) global = bucket;
            else individual = bucket;
        }
        return (now, global, individual);
    }

    private static async Task SaveBucketsAsync(DbConnection connection, DbTransaction transaction, LogSource source,
        TokenBucket.State global, TokenBucket.State individual, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Statement("global", global.Exists) + Statement("individual", individual.Exists);
        AddBucket(command, "global", 0, Guid.Empty, global);
        AddBucket(command, "individual", (byte)source.Kind, source.Id, individual);
        await command.ExecuteNonQueryAsync(token);
    }

    private static string Statement(string prefix, bool exists) => exists
        ? $"UPDATE [logquota].[Buckets] SET [Microtokens] = @{prefix}Tokens, [LastRefill] = @{prefix}Last, [PolicyVersion] = @{prefix}Version WHERE [Kind] = @{prefix}Kind AND [SourceId] = @{prefix}Source;"
        : $"INSERT INTO [logquota].[Buckets] ([Kind], [SourceId], [Microtokens], [LastRefill], [PolicyVersion]) VALUES (@{prefix}Kind, @{prefix}Source, @{prefix}Tokens, @{prefix}Last, @{prefix}Version);";

    private static void AddBucket(DbCommand command, string prefix, byte kind, Guid source, TokenBucket.State bucket)
    {
        Add(command, $"@{prefix}Kind", DbType.Byte, kind); Add(command, $"@{prefix}Source", DbType.Guid, source);
        Add(command, $"@{prefix}Tokens", DbType.Int64, bucket.Tokens); Add(command, $"@{prefix}Last", DbType.DateTimeOffset, bucket.LastRefill);
        Add(command, $"@{prefix}Version", DbType.Binary, bucket.PolicyVersion, 8);
    }

    private static void Add(DbCommand command, string name, DbType type, object value, int size = 0)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.DbType = type; parameter.Value = value;
        if (size != 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }

    private sealed record Policy(int SourceRate, int SourceBurst, int GlobalRate, int GlobalBurst, byte[] Version, DateTimeOffset UpdatedAt);
}
