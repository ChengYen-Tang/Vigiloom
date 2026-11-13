using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

/// <summary>Reads the Agent's policy using the caller's authentication transaction.</summary>
public sealed class IntegritySettingsReader : IIntegritySettingsReader
{
    public async Task<IntegritySettingsSnapshot> ReadAsync(DbConnection connection, DbTransaction transaction, Guid deviceId, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var policy = await db.IntegrityPolicyStates.FromSqlInterpolated($"SELECT * FROM [security].[IntegrityPolicyStates] WITH (HOLDLOCK) WHERE [DeviceId] = {deviceId}")
            .AsNoTracking().SingleOrDefaultAsync(token);
        var rows = await db.IntegrityScopes.FromSqlInterpolated($"SELECT * FROM [security].[IntegrityScopes] WITH (HOLDLOCK) WHERE [DeviceId] = {deviceId} AND [RetiredAt] IS NULL")
            .AsNoTracking().ToListAsync(token);
        return IntegritySettingsSnapshot.Create(rows.Select(x => IntegrityConfiguration.View(x).Scope), policy?.Generation ?? 0);
    }
}
