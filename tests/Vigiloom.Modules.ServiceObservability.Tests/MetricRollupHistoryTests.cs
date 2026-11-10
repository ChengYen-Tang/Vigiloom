using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricRollupHistoryTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task WindowHistorySeparatesQualityStatesAndPagesWithoutGaps() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var newest = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300);
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), now.AddDays(-1));
            var other = new MonitoredService(new ServiceCandidate(source.Id, "other", "prod", "Other", now), now.AddDays(-1));
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                setup.Sources.Add(source); setup.Services.AddRange(service, other);
                for (var i = 0; i < 105; i++)
                {
                    var state = new MetricRollupWindow { ServiceId = service.Id, WindowStart = newest.AddMinutes(-5 * i), InputRevision = i % 5 == 2 ? 2 : 1 };
                    if (i % 5 != 0)
                    {
                        state.ProjectedRevision = 1; state.ProjectionVersion = i % 5 == 4 ? 2 : 1;
                        state.ProjectedAt = now; state.IsComplete = i % 5 != 3; state.RawPruned = i == 1;
                    }
                    setup.MetricRollupWindows.Add(state);
                }
                setup.MetricRollupWindows.Add(new MetricRollupWindow { ServiceId = other.Id, WindowStart = newest, InputRevision = 1 });
                await setup.SaveChangesAsync(token);
            }
            var history = new TelemetryHistory(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            var from = newest.AddDays(-2); var to = newest.AddMinutes(5);
            var first = await history.MetricWindowsAsync(service.Id, from, to, null, token);
            Assert.HasCount(100, first.Items);
            Assert.IsNotNull(first.NextBefore);
            CollectionAssert.AreEqual(new[] { "Pending", "Current", "Stale", "Incomplete", "UnsupportedVersion" },
                first.Items.Take(5).Select(x => x.Status).ToArray());
            Assert.IsTrue(first.Items[1].RawPruned);
            var second = await history.MetricWindowsAsync(service.Id, from, to, first.NextBefore, token);
            Assert.HasCount(5, second.Items);
            Assert.IsNull(second.NextBefore);
            Assert.HasCount(105, first.Items.Concat(second.Items).Select(x => x.WindowStart).Distinct().ToArray());
            Assert.IsEmpty((await history.MetricWindowsAsync(Guid.CreateVersion7(), from, to, null, token)).Items);
            await Assert.ThrowsAsync<ArgumentException>(() => history.MetricWindowsAsync(service.Id, from, to, to.AddMinutes(1), token));
            await Assert.ThrowsAsync<ArgumentException>(() => history.MetricWindowsAsync(service.Id, from.AddDays(-366), to, null, token));
        }, context.CancellationToken);
}
