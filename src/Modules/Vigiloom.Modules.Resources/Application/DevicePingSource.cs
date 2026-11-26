using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public sealed class DevicePingSource(IDbContextFactory<ResourcesDbContext> contexts) : IDevicePingSource
{
    public async Task<PingDeviceContext> GetContextAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        return await GetContextAsync(id, db.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
    }

    public async Task<PingDeviceContext?> VerifyAsync(PingTargetVerification target, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var connection = db.Database.GetDbConnection();
        var current = await IsCurrentAsync(target.DeviceId, target.Version, target.Target, connection, transaction.GetDbTransaction(), token);
        if (!current) return null;
        return await GetContextAsync(target.DeviceId, connection, transaction.GetDbTransaction(), token);
    }

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

    public async Task<PingDeviceContext> GetContextAsync(Guid id, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<ResourcesDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        await using var db = new ResourcesDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var name = await db.Devices.AsNoTracking().Where(x => x.Id == id).Select(x => x.Name).SingleAsync(token);
        var groups = await (from member in db.DeviceGroups.AsNoTracking()
                            join item in db.Groups.AsNoTracking() on member.GroupId equals item.Id
                            where member.DeviceId == id
                            orderby item.Name
                            select new ResourceGroupSnapshot(item.Id, item.Name)).ToArrayAsync(token);
        return new(name, groups);
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.DbType = type; parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
