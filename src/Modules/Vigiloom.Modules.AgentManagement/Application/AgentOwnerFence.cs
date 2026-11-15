using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Uses the Agent lifecycle lock and the device generation fence in the caller's transaction.</summary>
public sealed class AgentOwnerFence(IDeviceAgentSource devices) : IAgentOwnerFence
{
    public async Task<bool> LockCurrentAsync(DbConnection connection, DbTransaction transaction, InventoryOwner owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.AgentId.Version != 7 || owner.DeviceId.Version != 7 || owner.DeviceGeneration < 0)
            throw new ArgumentException("A valid Agent and device generation are required.", nameof(owner));
        var options = new DbContextOptionsBuilder<AgentManagementDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options;
        await using var db = new AgentManagementDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        if (!await AgentDeviceLock.TryAcquireAsync(db, owner.DeviceId, token)) return false;
        var valid = await db.Agents.AsNoTracking().AnyAsync(x => x.Id == owner.AgentId && x.DeviceId == owner.DeviceId &&
            x.DeviceGeneration == owner.DeviceGeneration && x.RevokedAt == null, token);
        return valid && await devices.IsCurrentAsync(owner.DeviceId, owner.DeviceGeneration, connection, transaction, token);
    }
}
