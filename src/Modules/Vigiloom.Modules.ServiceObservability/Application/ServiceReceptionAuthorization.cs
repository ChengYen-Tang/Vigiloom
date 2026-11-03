using System.Data;
using System.Data.Common;
using Vigiloom.Modules.ServiceObservability.Domain;

namespace Vigiloom.Modules.ServiceObservability.Application;

/// <summary>Locks the current source and approved binding for the caller's telemetry transaction.</summary>
public static class ServiceReceptionAuthorization
{
    /// <summary>
    /// The source ID must come from a successfully authenticated source credential. The caller must
    /// retain the same open connection and transaction through its telemetry write and commit.
    /// A null result does not authorize a write and can still hold locks. End the transaction before
    /// attempting discovery, which acquires locks in a different order. Never cache or reuse a binding
    /// across transactions. Parse bounded input before taking locks; do not hold them during network IO.
    /// This method does not authenticate credentials.
    /// </summary>
    public static async Task<ServiceReceptionBinding?> LockAsync(DbConnection connection, DbTransaction transaction,
        Guid authenticatedSourceId, string serviceKey, string environment, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("An open connection and its active transaction are required.");
        if (authenticatedSourceId.Version != 7) return null;
        serviceKey = ServiceCandidate.ValidText(serviceKey, 128, nameof(serviceKey));
        environment = ServiceCandidate.ValidText(environment, 64, nameof(environment));

        // Lock the source first, matching discovery/approval/revocation order. HOLDLOCK retains
        // the lock until this same transaction commits or rolls back, including under RCSI.
        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = "SELECT [Id] FROM [service].[Sources] WITH (HOLDLOCK) WHERE [Id] = @source AND [RevokedAt] IS NULL";
            Add(source, "@source", DbType.Guid, authenticatedSourceId);
            if (await source.ExecuteScalarAsync(token) is null) return null;
        }
        await using var service = connection.CreateCommand();
        service.Transaction = transaction;
        service.CommandText = "SELECT [Id], [ApprovedAt] FROM [service].[Services] WITH (HOLDLOCK) WHERE [SourceId] = @source AND [ServiceKey] = @key AND [Environment] = @environment AND [RevokedAt] IS NULL";
        Add(service, "@source", DbType.Guid, authenticatedSourceId);
        Add(service, "@key", DbType.String, serviceKey, 128);
        Add(service, "@environment", DbType.String, environment, 64);
        await using var reader = await service.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new ServiceReceptionBinding(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)) : null;
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

public sealed record ServiceReceptionBinding(Guid ServiceId, DateTimeOffset ApprovedAt);
