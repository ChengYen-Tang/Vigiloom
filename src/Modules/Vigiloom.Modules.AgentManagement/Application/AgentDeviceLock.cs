using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Serializes Agent operations for one device within the caller's transaction.</summary>
internal static class AgentDeviceLock
{
    public static async Task<bool> TryAcquireAsync(AgentManagementDbContext db, Guid deviceId, CancellationToken token)
        => await TryAcquireAsync(db, $"vigiloom.agent.device.{deviceId:D}", token);

    private static async Task<bool> TryAcquireAsync(DbContext db, string resource, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.DbType = DbType.String;
        parameter.Value = resource; command.Parameters.Add(parameter);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        if (result == -1) return false;
        if (result < 0) throw new InvalidOperationException($"Agent device lock failed with status {result}.");
        return true;
    }

    public static async Task AcquireAsync(AgentManagementDbContext db, Guid deviceId, CancellationToken token)
    {
        if (!await TryAcquireAsync(db, deviceId, token)) throw new InvalidOperationException("Agent device lock could not be acquired.");
    }

    public static async Task AcquireReceiptAsync(HostMonitoringDbContext db, Guid agentId, CancellationToken token)
    {
        if (!await TryAcquireAsync(db, $"vigiloom.agent.receipt.{agentId:D}", token))
            throw new InvalidOperationException("Agent receipt lock could not be acquired.");
    }
}
