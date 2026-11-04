using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TraceIngestionTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task TraceExport_QuotaRetryRollsBackWholeBatchAndThenSucceeds() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var quotaOptions = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "api", "prod", "API", now), now.AddMinutes(-1));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token);
            }
            await using (var db = new LogAdmissionDbContext(quotaOptions))
            {
                await db.Database.MigrateAsync(token);
                var id = Guid.CreateVersion7();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [logquota].[TracePolicies] ([Id],[Name],[SourceRate],[SourceBurst],[GlobalRate],[GlobalBurst],[CreatedAt],[UpdatedAt]) VALUES ({id},{"central"},{1},{100},{1},{100},TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await TraceAdmission.AdmitAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), [new(source.Id, service.Id, 1)], token)).Status);
                await tx.CommitAsync(token);
                await db.Database.ExecuteSqlRawAsync("UPDATE [logquota].[TraceBuckets] SET [Microtokens] = 60000000, [LastRefill] = TODATETIMEOFFSET(DATEADD(hour, 1, SYSUTCDATETIME()), '+00:00')", token);
            }
            var resource = new ResourceSpans { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "api" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "node" } });
            var nanos = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000;
            foreach (var count in new[] { 60, 40 })
            {
                var scope = new ScopeSpans();
                for (var i = 0; i < count; i++) scope.Spans.Add(new Span { TraceId = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 16).ToArray()),
                    SpanId = ByteString.CopyFrom(Enumerable.Repeat((byte)2, 8).ToArray()), StartTimeUnixNano = nanos, EndTimeUnixNano = nanos });
                resource.ScopeSpans.Add(scope);
            }
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var export = new TraceExport(factory, new CandidateDiscovery(factory));
            var request = new ExportTraceServiceRequest { ResourceSpans = { resource } };
            var denied = await export.WriteAsync(source.Id, request, token);
            Assert.AreEqual(ExportStatus.Retry, denied.Status);
            Assert.IsNotNull(denied.RetryAfter);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await db.TraceSpans.CountAsync(token));
                Assert.AreEqual(0, await db.TraceContexts.CountAsync(token));
                Assert.AreEqual(0, await db.Instances.CountAsync(token));
            }
            await using (var db = new LogAdmissionDbContext(quotaOptions))
            {
                Assert.AreEqual(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[TraceBuckets] WHERE [Microtokens] = 60000000").SingleAsync(token));
                await db.Database.ExecuteSqlRawAsync("UPDATE [logquota].[TraceBuckets] SET [Microtokens] = 100000000", token);
            }
            Assert.AreEqual(ExportStatus.Complete, (await export.WriteAsync(source.Id, request, token)).Status);
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(100, await check.TraceSpans.CountAsync(token));
            Assert.AreEqual(1, await check.Instances.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task TraceIngestion_RequiresApprovalAndRollsBackOnWriteFailure() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var quotaOptions = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var other = new ServiceSource("other", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "api", "prod", "API", now), now.AddMinutes(-1));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.AddRange(source, other); db.Services.Add(service); await db.SaveChangesAsync(token);
            }
            await using (var db = new LogAdmissionDbContext(quotaOptions)) await db.Database.MigrateAsync(token);
            var resource = new ResourceSpans { Resource = new Resource(), SchemaUrl = "resource-schema" };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "api" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "node" } });
            var scope = new ScopeSpans { SchemaUrl = "scope-schema" };
            var span = new Span { Name = "operation", TraceId = ByteString.CopyFrom(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray()),
                SpanId = ByteString.CopyFrom(Enumerable.Range(1, 8).Select(i => (byte)i).ToArray()),
                StartTimeUnixNano = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000,
                EndTimeUnixNano = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000 };
            scope.Spans.Add(span);
            var ingestion = new TraceIngestion(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            Assert.AreEqual(TraceIngestionStatus.Unauthorized, await ingestion.WriteAsync(other.Id, resource, scope, token));
            span.TraceId = ByteString.CopyFrom(new byte[16]);
            Assert.AreEqual(TraceIngestionStatus.Invalid, await ingestion.WriteAsync(source.Id, resource, scope, token));
            span.TraceId = ByteString.CopyFrom(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
            span.StartTimeUnixNano = (ulong)now.AddMinutes(-2).ToUnixTimeMilliseconds() * 1_000_000;
            Assert.AreEqual(TraceIngestionStatus.BeforeApproval, await ingestion.WriteAsync(source.Id, resource, scope, token));
            span.StartTimeUnixNano = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000;
            span.EndTimeUnixNano = (ulong)now.AddHours(1).ToUnixTimeMilliseconds() * 1_000_000;
            Assert.AreEqual(TraceIngestionStatus.FutureTimestamp, await ingestion.WriteAsync(source.Id, resource, scope, token));
            span.EndTimeUnixNano = span.StartTimeUnixNano;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await db.TraceSpans.CountAsync(token));
                Assert.AreEqual(0, await db.Instances.CountAsync(token));
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [service].[FailSpanInsert] ON [service].[TraceSpans] INSTEAD OF INSERT AS THROW 51004, 'test failure', 1", token);
            }
            await Assert.ThrowsAsync<DbUpdateException>(() => ingestion.WriteAsync(source.Id, resource, scope, token));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await db.TraceSpans.CountAsync(token));
                Assert.AreEqual(0, await db.TraceContexts.CountAsync(token));
                Assert.AreEqual(0, await db.Instances.CountAsync(token));
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [service].[FailSpanInsert]", token);
            }
            await using (var db = new LogAdmissionDbContext(quotaOptions))
                Assert.AreEqual(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[TraceBuckets]").SingleAsync(token));
            Assert.AreEqual(TraceIngestionStatus.Accepted, await ingestion.WriteAsync(source.Id, resource, scope, token));
            await using var check = new ServiceObservabilityDbContext(options);
            var saved = await check.TraceSpans.Include(x => x.Context).SingleAsync(token);
            Assert.AreEqual(service.Id, saved.ServiceId);
            Assert.IsNotNull(saved.InstanceId);
            CollectionAssert.AreEqual(span.TraceId.ToByteArray(), saved.TraceId);
            Assert.AreEqual("operation", Span.Parser.ParseFrom(saved.SpanPayload).Name);
            Assert.AreEqual("scope-schema", saved.Context.ScopeSchemaUrl);
            await using var quota = new LogAdmissionDbContext(quotaOptions);
            Assert.AreEqual(2, await quota.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[TraceBuckets]").SingleAsync(token));
            await quota.Database.ExecuteSqlRawAsync("UPDATE [logquota].[TraceBuckets] SET [Microtokens] = 0, [LastRefill] = TODATETIMEOFFSET(DATEADD(hour, 1, SYSUTCDATETIME()), '+00:00')", token);
            Assert.AreEqual(TraceIngestionStatus.RateLimited, await ingestion.WriteAsync(source.Id, resource, scope, token));
            Assert.AreEqual(1, await check.TraceSpans.CountAsync(token));
        }, context.CancellationToken);
}
