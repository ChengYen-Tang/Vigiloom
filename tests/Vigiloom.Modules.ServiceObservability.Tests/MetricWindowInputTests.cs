using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricWindowInputTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ReadsBoundaryCandidatesWithoutReturningTruncatedInputs() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync( async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), now.AddDays(-1));
            var other = new MonitoredService(new ServiceCandidate(source.Id, "other", "prod", "Other", now), now.AddDays(-1));
            Assert.IsTrue(MetricWindow.TryForSample((ulong)now.ToUnixTimeMilliseconds() * 1_000_000, out var window));
            var start = DateTimeOffset.UnixEpoch.AddTicks((long)(window.Start / 100));
            var end = DateTimeOffset.UnixEpoch.AddTicks((long)(window.End / 100));
            await using var db = new ServiceObservabilityDbContext(options);
            await db.Database.MigrateAsync(token);
            db.Sources.Add(source);
            db.Services.AddRange(service, other);
            MetricPoint Point(Guid serviceId, DateTimeOffset time)
            {
                var point = new NumberDataPoint { TimeUnixNano = (ulong)(time - DateTimeOffset.UnixEpoch).Ticks * 100, AsInt = 1 };
                return new MetricPoint { ServiceId = serviceId, Kind = 1, OccurredAt = time, ReceivedAt = now,
                    PointPayload = point.ToByteArray(), Context = new MetricContext { ServiceId = serviceId,
                        ResourcePayload = [], ScopePayload = [], MetricPayload = [], ResourceSchemaUrl = "", ScopeSchemaUrl = "" } };
            }
            db.MetricPoints.AddRange(Point(service.Id, start.AddTicks(-1)), Point(service.Id, start),
                Point(service.Id, end), Point(service.Id, end.AddTicks(1)), Point(other.Id, start));
            await db.SaveChangesAsync(token);

            var result = await MetricWindowInputs.ReadAsync(db, service.Id, window, token);
            Assert.IsFalse(result.CapacityExceeded);
            Assert.HasCount(2, result.Points);
            CollectionAssert.AreEqual(new[] { start, end }, result.Points.Select(x => x.OccurredAt).ToArray());
            var tooMany = await MetricWindowInputs.ReadAsync(db, service.Id, window, token, maxPoints: 1);
            Assert.IsTrue(tooMany.CapacityExceeded);
            Assert.IsEmpty(tooMany.Points);
            var tooLarge = await MetricWindowInputs.ReadAsync(db, service.Id, window, token, maxBytes: 1);
            Assert.IsTrue(tooLarge.CapacityExceeded);
            Assert.IsEmpty(tooLarge.Points);
        }, context.CancellationToken);
}
