using System.Data;
using System.Data.Common;
using System.Text;

namespace Vigiloom.Modules.ServiceObservability.Application;

public enum InstanceResolutionStatus { Resolved, Unidentified, Unauthorized, Retired, CapacityReached, Busy }

public sealed record InstanceResolution(InstanceResolutionStatus Status, ServiceReceptionBinding? Service, Guid? InstanceId);

/// <summary>Central, trusted admission policy; never populate from a telemetry request.</summary>
public sealed record InstanceAdmissionLimits(int PerService = 1_000, int Total = 100_000);

/// <summary>Resolves instance identity within an authenticated receiver's write transaction.</summary>
public static class ServiceInstanceResolver
{
    private static readonly Encoding StrictUnicode = new UnicodeEncoding(false, false, true);
    private static readonly InstanceAdmissionLimits DefaultLimits = new();

    /// <remarks>
    /// The source comes from credential authentication, not telemetry. Keep this connection and
    /// transaction through the write and commit; end it before discovery or network operations.
    /// A transaction must process only one logical service, including all resolver calls: multiple
    /// services in one transaction can acquire admission and service locks in conflicting order.
    /// No result authorizes an event older than the returned service's approval time.
    /// </remarks>
    public static async Task<InstanceResolution> ResolveAsync(DbConnection connection, DbTransaction transaction,
        Guid authenticatedSourceId, string serviceKey, string environment, string? externalId, CancellationToken token,
        InstanceAdmissionLimits? limits = null)
    {
        limits ??= DefaultLimits;
        if (limits.PerService <= 0 || limits.Total <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
        byte[]? key = null;
        if (!string.IsNullOrEmpty(externalId))
        {
            if (externalId.Length > 256) throw new ArgumentException("Instance ID exceeds 256 UTF-16 code units.", nameof(externalId));
            try { key = StrictUnicode.GetBytes(externalId); }
            catch (EncoderFallbackException ex) { throw new ArgumentException("Instance ID must be valid Unicode.", nameof(externalId), ex); }
        }

        var service = await ServiceReceptionAuthorization.LockAsync(connection, transaction, authenticatedSourceId, serviceKey, environment, token);
        if (service is null) return new(InstanceResolutionStatus.Unauthorized, null, null);
        if (key is null) return new(InstanceResolutionStatus.Unidentified, service, null);

        // All instance mutations must use this same order: authorization, service lock, global
        // admission lock only for creation. Existing instances do not take the global lock.
        if (!await LockAsync(connection, transaction, $"Vigiloom.ServiceInstances.{service.ServiceId:D}", token))
            return new(InstanceResolutionStatus.Busy, service, null);

        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT TOP (1) [Id], [RetiredAt] FROM [service].[Instances] WHERE [ServiceId] = @service AND [SourceScopeId] = @source AND [ExternalInstanceIdBytes] = @key ORDER BY CASE WHEN [RetiredAt] IS NULL THEN 0 ELSE 1 END";
            Add(find, "@service", DbType.Guid, service.ServiceId);
            Add(find, "@source", DbType.Guid, authenticatedSourceId);
            Add(find, "@key", DbType.Binary, key, 512);
            await using var reader = await find.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
                return reader.IsDBNull(1) ? new(InstanceResolutionStatus.Resolved, service, reader.GetGuid(0)) : new(InstanceResolutionStatus.Retired, service, null);
        }

        if (!await LockAsync(connection, transaction, "Vigiloom.ServiceInstances.Admission", token))
            return new(InstanceResolutionStatus.Busy, service, null);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            IF (SELECT COUNT_BIG(*) FROM [service].[Instances] WHERE [ServiceId] = @service) >= @serviceLimit
               OR (SELECT COUNT_BIG(*) FROM [service].[Instances]) >= @totalLimit
                SELECT CAST(NULL AS uniqueidentifier);
            ELSE
            BEGIN
                INSERT INTO [service].[Instances] ([Id], [ServiceId], [SourceScopeId], [ExternalInstanceId], [CreatedAt], [UpdatedAt])
                VALUES (@id, @service, @source, @external, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));
                SELECT @id;
            END
            """;
        var id = Guid.CreateVersion7();
        Add(insert, "@id", DbType.Guid, id);
        Add(insert, "@service", DbType.Guid, service.ServiceId);
        Add(insert, "@source", DbType.Guid, authenticatedSourceId);
        Add(insert, "@external", DbType.String, externalId!, 256);
        Add(insert, "@serviceLimit", DbType.Int32, limits.PerService);
        Add(insert, "@totalLimit", DbType.Int32, limits.Total);
        var inserted = await insert.ExecuteScalarAsync(token);
        return inserted is Guid created ? new(InstanceResolutionStatus.Resolved, service, created) : new(InstanceResolutionStatus.CapacityReached, service, null);
    }

    private static async Task<bool> LockAsync(DbConnection connection, DbTransaction transaction, string resource, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
        Add(command, "@resource", DbType.String, resource, 255);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) >= 0;
    }

    private static void Add(DbCommand command, string name, DbType type, object value, int size = 0)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        if (size != 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }
}
