using System.Data;
using System.Data.Common;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Serializes inventory quota, staging, publication and cleanup per Agent transaction.</summary>
internal static class InventoryAgentLock
{
    internal static async Task<bool> TryAcquireAsync(DbConnection connection, DbTransaction transaction, Guid agentId,
        int timeoutMilliseconds, CancellationToken token)
    {
        if (agentId.Version != 7) throw new ArgumentException("A version 7 Agent ID is required.", nameof(agentId));
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("An open connection and its transaction are required.");
        if (timeoutMilliseconds is < 0 or > 30_000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout; SELECT @result";
        var resource = command.CreateParameter();
        resource.ParameterName = "@resource"; resource.DbType = DbType.String; resource.Size = 255;
        resource.Value = $"Vigiloom.Inventory.{agentId:D}";
        command.Parameters.Add(resource);
        var timeout = command.CreateParameter();
        timeout.ParameterName = "@timeout"; timeout.DbType = DbType.Int32; timeout.Value = timeoutMilliseconds;
        command.Parameters.Add(timeout);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        if (result >= 0) return true;
        if (result == -1) return false;
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException($"Inventory transaction lock failed with status {result}.");
    }
}
