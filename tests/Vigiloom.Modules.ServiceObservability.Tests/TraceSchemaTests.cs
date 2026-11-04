using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TraceSchemaTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Traces_PreserveProtocolIdsAndPreventWrongContextAndDestructiveRollback() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var first = new MonitoredService(new ServiceCandidate(source.Id, "one", "prod", "One", now), now);
            var second = new MonitoredService(new ServiceCandidate(source.Id, "two", "prod", "Two", now), now);
            var traceId = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            var spanId = Enumerable.Range(0, 8).Select(i => (byte)(i + 1)).ToArray();
            long contextId;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.AddRange(first, second);
                var resource = new TraceContext { ServiceId = first.Id, ResourcePayload = [1], ScopePayload = [2], ResourceSchemaUrl = "r", ScopeSchemaUrl = "s" };
                db.TraceSpans.Add(new TraceSpan { ServiceId = first.Id, Context = resource, StartedAt = now, EndedAt = now,
                    ReceivedAt = now, TraceId = traceId, SpanId = spanId, SpanPayload = [3] });
                await db.SaveChangesAsync(token);
                contextId = resource.RowId;
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var saved = await db.TraceSpans.AsNoTracking().Include(x => x.Context).SingleAsync(token);
                CollectionAssert.AreEqual(traceId, saved.TraceId);
                CollectionAssert.AreEqual(spanId, saved.SpanId);
                Assert.AreEqual("r", saved.Context.ResourceSchemaUrl);
                db.TraceSpans.Add(new TraceSpan { ServiceId = second.Id, ContextId = contextId, StartedAt = now, EndedAt = now,
                    ReceivedAt = now, TraceId = traceId, SpanId = spanId, SpanPayload = [3] });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
            var history = new TraceHistory(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            Assert.HasCount(1, (await history.QueryAsync(first.Id, now.AddMinutes(-1), now.AddMinutes(1), 1, token, traceId)).Items);
            Assert.IsEmpty((await history.QueryAsync(second.Id, now.AddMinutes(-1), now.AddMinutes(1), 1, token, traceId)).Items);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => history.QueryAsync(first.Id, now.AddDays(-32), now, 1, token));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => history.QueryAsync(first.Id, now.AddMinutes(-1), now.AddMinutes(1), 1, token, new byte[16]));
            await using (var db = new ServiceObservabilityDbContext(options))
                await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261104030858_LogObservedTime", token));
            await using var verify = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(1, await verify.TraceSpans.CountAsync(token));
        }, context.CancellationToken);
}
