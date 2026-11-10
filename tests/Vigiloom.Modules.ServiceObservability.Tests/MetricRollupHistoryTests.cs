using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
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
            var instance = Guid.CreateVersion7();
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
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instance},{service.Id},{source.Id},{"node-a"},{now},{now})", token);
                var target = setup.MetricRollupWindows.Local.Single(x => x.ServiceId == service.Id && x.WindowStart == newest.AddMinutes(-5));
                for (var i = 0; i < 55; i++)
                {
                    var hash = new byte[32]; BitConverter.GetBytes(i).CopyTo(hash, 0);
                    setup.MetricRollupSeries.Add(new MetricRollupSeries { WindowRowId = target.RowId, ServiceId = service.Id,
                        InstanceId = i <= 1 ? instance : null, IdentityHash = hash, IdentityBytes = [1], CollisionOrdinal = 0,
                        Mode = i == 0 ? MetricRollupMode.Unsupported : MetricRollupMode.GaugeSnapshot,
                        QualityFlags = i == 0 ? (long)MetricRollupQuality.Unsupported | 1L << 40 : 0,
                        RequiresRaw = i == 0, ResultVersion = i == 2 ? 2 : 1, InputCount = 1, ResultPayload = i == 0 ? null : [1, 2] });
                }
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
            var future = first.Items[4];
            Assert.AreEqual(MetricRollupReadStatus.UnsupportedVersion,
                (await history.MetricSeriesAsync(service.Id, future.WindowStart, future.VersionToken, 1, null, token)).Status);
            Assert.AreEqual(MetricRollupReadStatus.UnsupportedVersion,
                (await history.MetricPayloadAsync(service.Id, future.WindowStart, future.VersionToken, 1, 0, null, token)).Status);
            var selected = first.Items[1];
            var series = await history.MetricSeriesAsync(service.Id, selected.WindowStart, selected.VersionToken, 1, null, token);
            Assert.AreEqual(MetricRollupReadStatus.Found, series.Status);
            Assert.IsNotNull(series.Page);
            Assert.HasCount(50, series.Page.Items);
            Assert.IsTrue(series.Page.HasMore);
            Assert.AreEqual(instance, series.Page.Items[0].InstanceId);
            Assert.Contains("Unsupported", series.Page.Items[0].QualityReasons);
            Assert.Contains("Unknown", series.Page.Items[0].QualityReasons);
            Assert.IsNull(series.Page.Items[0].PayloadBytes);
            Assert.AreEqual(2, series.Page.Items[1].PayloadBytes);
            Assert.AreEqual(MetricRollupReadStatus.NotFound,
                (await history.MetricPayloadAsync(service.Id, selected.WindowStart, selected.VersionToken, 1, 0, null, token)).Status);
            var payload = await history.MetricPayloadAsync(service.Id, selected.WindowStart, selected.VersionToken, 1, 1, null, token);
            Assert.AreEqual(MetricRollupReadStatus.Found, payload.Status);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, payload.Payload);
            Assert.AreEqual(MetricRollupReadStatus.UnsupportedVersion,
                (await history.MetricPayloadAsync(service.Id, selected.WindowStart, selected.VersionToken, 1, 2, null, token)).Status);
            await Assert.ThrowsAsync<ArgumentException>(() => history.MetricPayloadAsync(service.Id, selected.WindowStart, selected.VersionToken, 1, 50, null, token));
            var filtered = await history.MetricSeriesAsync(service.Id, selected.WindowStart, selected.VersionToken, 1, instance, token);
            Assert.HasCount(2, filtered.Page!.Items);
            var filteredPayload = await history.MetricPayloadAsync(service.Id, selected.WindowStart, selected.VersionToken, 1, 1, instance, token);
            Assert.AreEqual(MetricRollupReadStatus.Found, filteredPayload.Status);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, filteredPayload.Payload);
            var nextSeries = await history.MetricSeriesAsync(service.Id, selected.WindowStart, selected.VersionToken, 2, null, token);
            Assert.HasCount(5, nextSeries.Page!.Items);
            Assert.IsFalse(nextSeries.Page.HasMore);
            await using (var mutate = new ServiceObservabilityDbContext(options))
            {
                var updated = await mutate.MetricRollupWindows.SingleAsync(x => x.ServiceId == service.Id && x.WindowStart == selected.WindowStart, token);
                updated.InputRevision++;
                await mutate.SaveChangesAsync(token);
            }
            Assert.AreEqual(MetricRollupReadStatus.Changed,
                (await history.MetricSeriesAsync(service.Id, selected.WindowStart, selected.VersionToken, 2, null, token)).Status);
            Assert.AreEqual(MetricRollupReadStatus.Changed,
                (await history.MetricPayloadAsync(service.Id, selected.WindowStart, selected.VersionToken, 1, 1, null, token)).Status);
            Assert.AreEqual(MetricRollupReadStatus.NotFound,
                (await history.MetricSeriesAsync(Guid.CreateVersion7(), selected.WindowStart, selected.VersionToken, 1, null, token)).Status);
            await Assert.ThrowsAsync<ArgumentException>(() => history.MetricSeriesAsync(service.Id, selected.WindowStart, "bad", 1, null, token));
            var second = await history.MetricWindowsAsync(service.Id, from, to, first.NextBefore, token);
            Assert.HasCount(5, second.Items);
            Assert.IsNull(second.NextBefore);
            Assert.HasCount(105, first.Items.Concat(second.Items).Select(x => x.WindowStart).Distinct().ToArray());
            Assert.IsEmpty((await history.MetricWindowsAsync(Guid.CreateVersion7(), from, to, null, token)).Items);
            await Assert.ThrowsAsync<ArgumentException>(() => history.MetricWindowsAsync(service.Id, from, to, to.AddMinutes(1), token));
            await Assert.ThrowsAsync<ArgumentException>(() => history.MetricWindowsAsync(service.Id, from.AddDays(-366), to, null, token));
        }, context.CancellationToken);
}
