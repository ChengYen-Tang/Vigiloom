using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public sealed class DevicePingSource(IDbContextFactory<ResourcesDbContext> contexts) : IDevicePingSource
{
    public async IAsyncEnumerable<PingDeviceSnapshot> ReadEnabledAsync([EnumeratorCancellation] CancellationToken token)
    {
        long after = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var devices = await db.Devices.AsNoTracking().Where(x => x.RowId > after && x.IsEnabled && !x.IsArchived)
                .OrderBy(x => x.RowId).Take(250).Select(x => new { x.Id, x.RowId, x.Address, x.Version }).ToArrayAsync(token);
            if (devices.Length == 0) yield break;
            after = devices[^1].RowId;
            var ids = devices.Select(x => x.Id).ToArray();
            var targets = await db.DeviceTargets.AsNoTracking().Where(x => ids.Contains(x.DeviceId)).ToArrayAsync(token);
            var lookup = targets.ToLookup(x => x.DeviceId, x => x.Address);
            foreach (var device in devices)
                yield return new(device.Id, device.Version, lookup[device.Id].Prepend(device.Address).Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    public async Task<bool> IsCurrentAsync(Guid id, byte[] version, string target, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM resources.Devices d WITH (HOLDLOCK)
            WHERE d.Id = @id AND d.Version = @version AND d.IsEnabled = 1 AND d.IsArchived = 0
              AND (d.Address = @target OR EXISTS
                   (SELECT 1 FROM resources.DeviceTargets t WITH (HOLDLOCK) WHERE t.DeviceId = d.Id AND t.Address = @target))
            """;
        Add(command, "@id", DbType.Guid, id);
        Add(command, "@version", DbType.Binary, version);
        Add(command, "@target", DbType.String, target);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.DbType = type; parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
