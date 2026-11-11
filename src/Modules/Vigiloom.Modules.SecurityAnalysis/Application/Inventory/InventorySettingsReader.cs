using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Reads policy within an already authenticated Agent transaction.</summary>
public sealed class InventorySettingsReader : IInventorySettingsReader
{
    public async Task<InventorySettingsSnapshot> ReadAsync(DbConnection connection, DbTransaction transaction, Guid deviceId, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var rows = await db.InventorySettings.FromSqlInterpolated($"SELECT * FROM [security].[InventorySettings] WITH (HOLDLOCK) WHERE [DeviceId] IS NULL OR [DeviceId] = {deviceId}")
            .AsNoTracking().ToListAsync(token);
        return InventoryConfiguration.ToView(deviceId, rows).Effective;
    }
}
