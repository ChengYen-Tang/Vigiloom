using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InventoryConfigurationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Migration_DowngradePreservesCommandPolicyAndAudit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var previous = migrations.Single(x => x.EndsWith("_ProcessInventorySources", StringComparison.Ordinal));
            var migrator = db.GetService<IMigrator>();
            // A database without new data can move down and back up.
            await migrator.MigrateAsync(previous, token);
            await migrator.MigrateAsync(null, token);
            var service = new InventoryConfiguration(new Factory(options), new Devices(Guid.CreateVersion7()), TimeProvider.System);
            var platform = (await service.GetAsync(null, token))!;
            Assert.IsNotNull(await service.UpdateAsync(null, new(new(null, false), platform.Version), Guid.CreateVersion7(), token));
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous, token));
            await using var verify = new SecurityAnalysisDbContext(options);
            var saved = (await verify.InventorySettings.SingleAsync(token)).CollectCommandLine;
            Assert.IsNotNull(saved);
            Assert.IsFalse(saved.Value);
            Assert.IsFalse((await verify.InventorySettingAudits.SingleAsync(token)).EffectiveCollectCommandLine);
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Settings_InheritOverrideResetAndAuditWithConcurrencyProtection() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;

            var deviceId = Guid.CreateVersion7();
            var actorId = Guid.CreateVersion7();
            var service = new InventoryConfiguration(new Factory(options), new Devices(deviceId), TimeProvider.System);
            var platform = (await service.GetAsync(null, token))!;
            Assert.AreEqual(21600, platform.Effective.Settings.ScanIntervalSeconds);
            Assert.IsFalse(platform.Effective.Settings.CollectCommandLine);
            platform = (await service.UpdateAsync(null, new(new(3600, true), platform.Version), actorId, token))!;
            Assert.IsTrue(platform.Effective.Settings.CollectCommandLine);
            Assert.AreEqual(InventorySettingsSnapshot.ProcessSchema, platform.Effective.SchemaVersion);
            var device = (await service.GetAsync(deviceId, token))!;
            Assert.AreEqual(3600, device.Effective.Settings.ScanIntervalSeconds);
            Assert.IsTrue(device.Effective.Settings.CollectCommandLine);
            device = (await service.UpdateAsync(deviceId, new(new(7200, false), device.Version), actorId, token))!;
            Assert.AreEqual(7200, device.Effective.Settings.ScanIntervalSeconds);
            Assert.IsFalse(device.Effective.Settings.CollectCommandLine);
            Assert.IsNull(await service.UpdateAsync(deviceId, new(new(9000), ""), actorId, token));
            device = (await service.UpdateAsync(deviceId, new(new(null), device.Version), actorId, token))!;
            Assert.AreEqual(3600, device.Effective.Settings.ScanIntervalSeconds);
            Assert.IsTrue(device.Effective.Settings.CollectCommandLine);
            Assert.IsFalse(InventoryConfiguration.Valid(new(899)));
            Assert.IsFalse(InventoryConfiguration.Valid(new(604801)));
            await using var verify = new SecurityAnalysisDbContext(options);
            var audits = await verify.InventorySettingAudits.OrderBy(x => x.RowId).ToArrayAsync(token);
            Assert.HasCount(3, audits);
            Assert.AreEqual(7200, audits[1].EffectiveScanIntervalSeconds);
            Assert.AreEqual(3600, audits[2].EffectiveScanIntervalSeconds);
            Assert.IsTrue(audits[0].EffectiveCollectCommandLine);
            Assert.IsFalse(audits[1].EffectiveCollectCommandLine);
            Assert.IsTrue(audits[2].EffectiveCollectCommandLine);
            Assert.AreEqual(actorId, audits[2].ActorId);
        }, TestContext.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    private sealed class Devices(Guid deviceId) : IDeviceAgentSource
    {
        public Task<DeviceAgentState?> GetAsync(Guid id, CancellationToken token) =>
            Task.FromResult(id == deviceId ? new DeviceAgentState(id, 1, true) : null);
        public Task<bool> IsCurrentAsync(Guid id, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
            Task.FromResult(id == deviceId && generation == 1);
    }
}
