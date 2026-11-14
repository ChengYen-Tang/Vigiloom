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
public sealed class ReceptionOverviewTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task LatestPersistedSignalIsGroupedBySourceAndNotEventTime() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var sourceA = new ServiceSource("same name", new byte[32], now);
            var sourceB = new ServiceSource("same name", new byte[32], now);
            sourceB.Revoke(now);
            var first = new MonitoredService(new ServiceCandidate(sourceA.Id, "one", "prod", "One", now), now);
            var second = new MonitoredService(new ServiceCandidate(sourceA.Id, "two", "prod", "Two", now), now);
            var third = new MonitoredService(new ServiceCandidate(sourceB.Id, "three", "prod", "Three", now), now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Sources.AddRange(sourceA, sourceB); db.Services.AddRange(first, second, third);
                static LogContext Shared(Guid service) => new() { ServiceId = service, ResourcePayload = [], ScopePayload = [], ResourceSchemaUrl = "", ScopeSchemaUrl = "" };
                db.LogRecords.AddRange(
                    new LogRecord { ServiceId = first.Id, Context = Shared(first.Id), OccurredAt = now, ReceivedAt = now.AddMinutes(-10), RecordPayload = [] },
                    new LogRecord { ServiceId = second.Id, Context = Shared(second.Id), OccurredAt = now.AddDays(-1), ReceivedAt = now.AddMinutes(-1), RecordPayload = [] },
                    new LogRecord { ServiceId = third.Id, Context = Shared(third.Id), OccurredAt = now, ReceivedAt = now.AddMinutes(-3), RecordPayload = [] });
                await db.SaveChangesAsync(token);
            }
            var overview = new ReceptionOverview(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            var rows = await overview.ListAsync(1, token);
            Assert.HasCount(2, rows);
            var active = rows.Single(x => x.SourceId == sourceA.Id);
            var revoked = rows.Single(x => x.SourceId == sourceB.Id);
            Assert.AreEqual(now.AddMinutes(-1), active.LastLogAt);
            Assert.IsNull(active.LastMetricAt); Assert.IsNull(active.LastTraceAt);
            Assert.AreEqual(now.AddMinutes(-3), revoked.LastLogAt);
            Assert.IsNotNull(revoked.RevokedAt);
        }, context.CancellationToken);
}
