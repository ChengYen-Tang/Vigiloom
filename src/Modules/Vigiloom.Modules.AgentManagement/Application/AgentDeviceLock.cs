using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Serializes Agent operations for one device within the caller's transaction.</summary>
internal static class AgentDeviceLock
{
    public static async Task AcquireAsync(AgentManagementDbContext db, Guid deviceId, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.DbType = DbType.String;
        parameter.Value = $"vigiloom.agent.device.{deviceId:D}"; command.Parameters.Add(parameter);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0) throw new InvalidOperationException("Agent device lock could not be acquired.");
    }
}
