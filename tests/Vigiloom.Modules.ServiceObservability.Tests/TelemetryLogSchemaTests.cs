using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TelemetryLogSchemaTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task LogHistory_UsesServiceScopedBoundedTimeWindowAndStablePages() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "one", "prod", "One", now), now);
            var other = new MonitoredService(new ServiceCandidate(source.Id, "two", "prod", "Two", now), now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.AddRange(service, other);
                var shared = new LogContext { ServiceId = service.Id, ResourcePayload = [1], ScopePayload = [2], ResourceSchemaUrl = "", ScopeSchemaUrl = "" };
                var otherContext = new LogContext { ServiceId = other.Id, ResourcePayload = [1], ScopePayload = [2], ResourceSchemaUrl = "", ScopeSchemaUrl = "" };
                db.LogRecords.AddRange(Enumerable.Range(0, 27).Select(i => new LogRecord
                {
                    ServiceId = service.Id, OccurredAt = now, ReceivedAt = now,
                    Context = shared, RecordPayload = [(byte)i]
                }));
                db.LogRecords.Add(new LogRecord { ServiceId = other.Id, OccurredAt = now, ReceivedAt = now,
                    Context = otherContext, RecordPayload = [100] });
                await db.SaveChangesAsync(token);
            }
            var history = new LogHistory(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            var first = await history.QueryAsync(service.Id, now.AddMinutes(-1), now.AddMinutes(1), 1, token);
            var second = await history.QueryAsync(service.Id, now.AddMinutes(-1), now.AddMinutes(1), 2, token);
            Assert.HasCount(25, first.Items);
            Assert.IsTrue(first.HasMore);
            Assert.HasCount(2, second.Items);
            Assert.IsFalse(second.HasMore);
            Assert.HasCount(27, first.Items.Concat(second.Items).Select(x => x.RowId).Distinct().ToArray());
            Assert.IsEmpty((await history.QueryAsync(service.Id, now.AddMinutes(-1), now.AddMinutes(1), 1, token, Enumerable.Repeat((byte)1, 16).ToArray())).Items);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => history.QueryAsync(service.Id, now.AddMinutes(-1), now.AddMinutes(1), 1, token, new byte[16]));
            Assert.IsEmpty((await history.QueryAsync(service.Id, now.AddMinutes(1), now.AddMinutes(2), 1, token)).Items);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => history.QueryAsync(service.Id, now.AddDays(-32), now, 1, token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task LogSchema_PreservesTraceBytesAndRejectsCrossServiceInstance() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "one", "prod", "One", now), now);
            var other = new MonitoredService(new ServiceCandidate(source.Id, "two", "prod", "Two", now), now);
            Guid instanceId;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.AddRange(service, other);
                await db.SaveChangesAsync(token);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var instance = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
                    source.Id, "one", "prod", "node", token);
                instanceId = instance.InstanceId!.Value;
                await tx.CommitAsync(token);
            }
            var trace = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            var span = Enumerable.Range(16, 8).Select(i => (byte)i).ToArray();
            long contextId;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var logContext = new LogContext { ServiceId = service.Id, ResourcePayload = [1], ScopePayload = [2], ResourceSchemaUrl = "", ScopeSchemaUrl = "" };
                db.LogContexts.Add(logContext);
                await db.SaveChangesAsync(token);
                contextId = logContext.RowId;
            }
            LogRecord Create(Guid serviceId) => new()
            {
                ServiceId = serviceId, InstanceId = instanceId, ContextId = contextId, OccurredAt = now, ReceivedAt = now,
                TraceId = trace, SpanId = span, RecordPayload = [3]
            };
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.LogRecords.Add(Create(service.Id));
                await db.SaveChangesAsync(token);
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var saved = await db.LogRecords.AsNoTracking().SingleAsync(token);
                CollectionAssert.AreEqual(trace, saved.TraceId);
                CollectionAssert.AreEqual(span, saved.SpanId);
                Assert.AreEqual(service.Id, saved.ServiceId);
                db.LogRecords.Add(withInstanceCleared());
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var differentContext = new LogContext { ServiceId = other.Id, ResourcePayload = [1], ScopePayload = [2], ResourceSchemaUrl = "", ScopeSchemaUrl = "" };
                var invalidInstance = Create(other.Id);
                invalidInstance.Context = differentContext;
                invalidInstance.ContextId = 0;
                db.LogRecords.Add(invalidInstance);
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var contextRow = await db.LogContexts.SingleAsync(token);
                db.LogContexts.Remove(contextRow);
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
            LogRecord withInstanceCleared()
            {
                var record = Create(other.Id);
                record.InstanceId = null;
                return record;
            }
        }, context.CancellationToken);
}
