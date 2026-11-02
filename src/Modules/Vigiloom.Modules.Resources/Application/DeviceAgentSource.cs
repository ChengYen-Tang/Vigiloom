using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public sealed class DeviceAgentSource(IDbContextFactory<ResourcesDbContext> contexts) : IDeviceAgentSource
{
    public async Task<DeviceAgentState?> GetAsync(Guid deviceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Devices.AsNoTracking().Where(x => x.Id == deviceId)
            .Select(x => new DeviceAgentState(x.Id, x.AgentAuthorizationGeneration, x.DeepMonitoring && x.IsEnabled && !x.IsArchived))
            .SingleOrDefaultAsync(token);
    }

    public async Task<bool> IsCurrentAsync(Guid deviceId, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM resources.Devices d WITH (HOLDLOCK)
            WHERE d.Id = @id AND d.AgentAuthorizationGeneration = @generation
              AND d.HasAgent = 1 AND d.IsEnabled = 1 AND d.IsArchived = 0
            """;
        var id = command.CreateParameter(); id.ParameterName = "@id"; id.DbType = DbType.Guid; id.Value = deviceId; command.Parameters.Add(id);
        var version = command.CreateParameter(); version.ParameterName = "@generation"; version.DbType = DbType.Int64; version.Value = generation; command.Parameters.Add(version);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
    }
}
