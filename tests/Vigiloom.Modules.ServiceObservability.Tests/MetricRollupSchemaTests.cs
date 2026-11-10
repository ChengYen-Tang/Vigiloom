using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricRollupSchemaTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task MigrationEnforcesWindowAndSeriesSafety() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300);
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), now.AddDays(-1));
            var window = new MetricRollupWindow { ServiceId = service.Id, WindowStart = start, InputRevision = 1 };
            string previousMigration;
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                var applied = (await setup.Database.GetAppliedMigrationsAsync(token)).ToArray();
                previousMigration = applied[^2];
                var migrator = setup.GetService<IMigrator>();
                await migrator.MigrateAsync(previousMigration, token);
                await migrator.MigrateAsync(null, token);
                setup.Sources.Add(source);
                setup.Services.Add(service);
                setup.MetricRollupWindows.Add(window);
                await setup.SaveChangesAsync(token);
            }
            MetricRollupSeries Series(int ordinal, MetricRollupMode mode = MetricRollupMode.GaugeSnapshot, bool requiresRaw = false) => new()
            {
                WindowRowId = window.RowId, ServiceId = service.Id, IdentityHash = new byte[32], IdentityBytes = [(byte)(ordinal + 1)],
                CollisionOrdinal = ordinal, Mode = mode, RequiresRaw = requiresRaw, ResultVersion = 1,
                InputCount = 1, InputBytes = 2, ResultPayload = requiresRaw ? null : [1]
            };
            await using (var valid = new ServiceObservabilityDbContext(options))
            {
                // Equal hashes may denote distinct series; the application compares the full identities.
                valid.MetricRollupSeries.AddRange(Series(0), Series(1));
                await valid.SaveChangesAsync(token);
            }
            await using (var duplicate = new ServiceObservabilityDbContext(options))
            {
                duplicate.MetricRollupSeries.Add(Series(0));
                await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(token));
            }
            await using (var unsafeSeries = new ServiceObservabilityDbContext(options))
            {
                unsafeSeries.MetricRollupSeries.Add(Series(2, mode: MetricRollupMode.Unsupported));
                await Assert.ThrowsAsync<DbUpdateException>(() => unsafeSeries.SaveChangesAsync(token));
            }
            await using (var partialRevision = new ServiceObservabilityDbContext(options))
            {
                partialRevision.MetricRollupWindows.Add(new MetricRollupWindow { ServiceId = service.Id, WindowStart = start.AddMinutes(5),
                    InputRevision = 1, ProjectedRevision = 1, ProjectedAt = now });
                await Assert.ThrowsAsync<DbUpdateException>(() => partialRevision.SaveChangesAsync(token));
            }
            await using (var duplicateWindow = new ServiceObservabilityDbContext(options))
            {
                duplicateWindow.MetricRollupWindows.Add(new MetricRollupWindow { ServiceId = service.Id, WindowStart = start, InputRevision = 1 });
                await Assert.ThrowsAsync<DbUpdateException>(() => duplicateWindow.SaveChangesAsync(token));
            }
            await using (var unaligned = new ServiceObservabilityDbContext(options))
            {
                unaligned.MetricRollupWindows.Add(new MetricRollupWindow { ServiceId = service.Id, WindowStart = start.AddSeconds(1), InputRevision = 1 });
                await Assert.ThrowsAsync<DbUpdateException>(() => unaligned.SaveChangesAsync(token));
            }
            await using (var guard = new ServiceObservabilityDbContext(options))
            {
                var error = await Assert.ThrowsAsync<SqlException>(() => guard.GetService<IMigrator>().MigrateAsync(previousMigration, token));
                Assert.AreEqual(51000, error.Number);
                Assert.AreEqual(1, await guard.MetricRollupWindows.CountAsync(token));
                Assert.AreEqual(2, await guard.MetricRollupSeries.CountAsync(token));
            }
        }, context.CancellationToken);
}
