using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

internal sealed record ServiceKey(string Name, string Environment, string Namespace)
{
    internal static ServiceKey From(ResourceIdentity identity) => new(identity.ServiceKey, identity.Environment, identity.ServiceNamespace);
}

internal sealed record BatchAuthorizationResult(bool SourceUnavailable, Dictionary<ServiceKey, ServiceReceptionBinding?>? Bindings);

/// <summary>Acquires every service lock before any instance resolver can acquire global admission.</summary>
internal static class BatchAuthorization
{
    internal static ServiceKey[] Order(IEnumerable<ResourceIdentity> identities) => identities.Select(ServiceKey.From).Distinct()
        .OrderBy(x => x.Namespace, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal)
        .ThenBy(x => x.Environment, StringComparer.Ordinal).ToArray();

    internal static async Task<BatchAuthorizationResult> LockAsync(
        ServiceObservabilityDbContext db, IDbContextTransaction tx, Guid sourceId, IReadOnlyList<ServiceKey> keys, CancellationToken token, byte[]? version = null)
    {
        var connection = db.Database.GetDbConnection();
        var transaction = tx.GetDbTransaction();
        var bindings = new Dictionary<ServiceKey, ServiceReceptionBinding?>();
        if (version is not null)
        {
            await using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = "SELECT [Id] FROM [service].[Sources] WITH (HOLDLOCK) WHERE [Id] = @source AND [RevokedAt] IS NULL AND [Version] = @version";
            var id = source.CreateParameter(); id.ParameterName = "@source"; id.DbType = DbType.Guid; id.Value = sourceId; source.Parameters.Add(id);
            var v = source.CreateParameter(); v.ParameterName = "@version"; v.DbType = DbType.Binary; v.Size = 8; v.Value = version; source.Parameters.Add(v);
            if (await source.ExecuteScalarAsync(token) is null) return new(true, null);
        }
        foreach (var key in keys)
            bindings[key] = version is null
                ? await ServiceReceptionAuthorization.LockAsync(connection, transaction, sourceId, key.Name, key.Environment, token, key.Namespace)
                : await ServiceReceptionAuthorization.LockAsync(connection, transaction, new AuthenticatedSource(sourceId, version), key.Name, key.Environment, token, key.Namespace);
        await using (var policy = connection.CreateCommand())
        {
            policy.Transaction = transaction;
            policy.CommandText = "SELECT [PerService] FROM [service].[InstancePolicies] WITH (HOLDLOCK) WHERE [Name] = N'central'";
            await policy.ExecuteScalarAsync(token);
        }
        foreach (var id in bindings.Values.Where(x => x is not null).Select(x => x!.ServiceId).Distinct().Order())
            if (!await ServiceInstanceResolver.LockServiceAsync(connection, transaction, id, token)) return new(false, null);
        return new(false, bindings);
    }
}
