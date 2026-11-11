using System.Data.Common;
using Microsoft.EntityFrameworkCore;
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
    public Task Settings_InheritOverrideResetAndAuditWithConcurrencyProtection() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using (var db = new SecurityAnalysisDbContext(options)) await db.Database.MigrateAsync(token);
            var deviceId = Guid.CreateVersion7();
            var actorId = Guid.CreateVersion7();
            var service = new InventoryConfiguration(new Factory(options), new Devices(deviceId), TimeProvider.System);
            var platform = (await service.GetAsync(null, token))!;
            Assert.AreEqual(21600, platform.Effective.Settings.ScanIntervalSeconds);
            platform = (await service.UpdateAsync(null, new(new(3600), platform.Version), actorId, token))!;
            var device = (await service.GetAsync(deviceId, token))!;
            Assert.AreEqual(3600, device.Effective.Settings.ScanIntervalSeconds);
            device = (await service.UpdateAsync(deviceId, new(new(7200), device.Version), actorId, token))!;
            Assert.AreEqual(7200, device.Effective.Settings.ScanIntervalSeconds);
            Assert.IsNull(await service.UpdateAsync(deviceId, new(new(9000), ""), actorId, token));
            device = (await service.UpdateAsync(deviceId, new(new(null), device.Version), actorId, token))!;
            Assert.AreEqual(3600, device.Effective.Settings.ScanIntervalSeconds);
            Assert.IsFalse(InventoryConfiguration.Valid(new(899)));
            Assert.IsFalse(InventoryConfiguration.Valid(new(604801)));
            await using var verify = new SecurityAnalysisDbContext(options);
            var audits = await verify.InventorySettingAudits.OrderBy(x => x.RowId).ToArrayAsync(token);
            Assert.HasCount(3, audits);
            Assert.AreEqual(7200, audits[1].EffectiveScanIntervalSeconds);
            Assert.AreEqual(3600, audits[2].EffectiveScanIntervalSeconds);
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
