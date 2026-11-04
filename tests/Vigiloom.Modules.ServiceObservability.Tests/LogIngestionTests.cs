using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.LogAdmission;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using OtlpLogRecord = OpenTelemetry.Proto.Logs.V1.LogRecord;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LogIngestionTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Export_ConcurrentReverseServiceOrderCommitsBothRequests() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var quotaOptions = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source);
                db.Services.AddRange(new MonitoredService(new ServiceCandidate(source.Id, "alpha", "prod", "A", now), now.AddMinutes(-1)),
                    new MonitoredService(new ServiceCandidate(source.Id, "beta", "prod", "B", now), now.AddMinutes(-1)));
                await db.SaveChangesAsync(token);
            }
            await using (var db = new LogAdmissionDbContext(quotaOptions)) await db.Database.MigrateAsync(token);
            static ResourceLogs ResourceFor(string name)
            {
                var resource = new ResourceLogs { Resource = new Resource() };
                resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = name } });
                resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
                resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "node" } });
                resource.ScopeLogs.Add(new ScopeLogs { LogRecords = { new OtlpLogRecord() } });
                return resource;
            }
            var first = new ExportLogsServiceRequest { ResourceLogs = { ResourceFor("alpha"), ResourceFor("beta") } };
            var second = new ExportLogsServiceRequest { ResourceLogs = { ResourceFor("beta"), ResourceFor("alpha") } };
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var export = new LogExport(factory, new CandidateDiscovery(factory));
            var results = await Task.WhenAll(export.WriteAsync(source.Id, first, token), export.WriteAsync(source.Id, second, token));
            Assert.IsTrue(results.All(x => x.Status == ExportStatus.Complete));
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(4, await check.LogRecords.CountAsync(token));
            Assert.AreEqual(2, await check.Instances.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Export_RetryDoesNotCommitPrefixOrConsumeQuota() =>
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
                await using var tx = await db.Database.BeginTransactionAsync(token);
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Vigiloom.Infrastructure.LogAdmission.LogAdmission.AdmitAsync(
                    db.Database.GetDbConnection(), tx.GetDbTransaction(), new(LogSourceKind.Application, source.Id), 1, token)).Status);
                await tx.CommitAsync(token);
                await db.Database.ExecuteSqlRawAsync("UPDATE [logquota].[Buckets] SET [Microtokens] = 60000000, [LastRefill] = TODATETIMEOFFSET(DATEADD(hour, 1, SYSUTCDATETIME()), '+00:00')", token);
            }
            var resource = new ResourceLogs { Resource = new Resource() };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "api" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "node" } });
            foreach (var count in new[] { 60, 40 })
            {
                var scope = new ScopeLogs();
                for (var i = 0; i < count; i++) scope.LogRecords.Add(new OtlpLogRecord { Body = new AnyValue { StringValue = "record" } });
                resource.ScopeLogs.Add(scope);
            }
            var request = new ExportLogsServiceRequest { ResourceLogs = { resource } };
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var export = new LogExport(factory, new CandidateDiscovery(factory));
            var denied = await export.WriteAsync(source.Id, request, token);
            Assert.AreEqual(ExportStatus.Retry, denied.Status);
            Assert.IsNotNull(denied.RetryAfter);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await db.LogRecords.CountAsync(token));
                Assert.AreEqual(0, await db.LogContexts.CountAsync(token));
                Assert.AreEqual(0, await db.Instances.CountAsync(token));
            }
            await using (var db = new LogAdmissionDbContext(quotaOptions))
            {
                Assert.AreEqual(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[Buckets] WHERE [Microtokens] = 60000000").SingleAsync(token));
                await db.Database.ExecuteSqlRawAsync("UPDATE [logquota].[Buckets] SET [Microtokens] = 100000000", token);
            }
            resource.ScopeLogs[1].LogRecords.Add(new OtlpLogRecord());
            Assert.AreEqual(ExportStatus.BatchTooLarge, (await export.WriteAsync(source.Id, request, token)).Status);
            resource.ScopeLogs[1].LogRecords.RemoveAt(40);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await db.LogRecords.CountAsync(token));
                Assert.AreEqual(0, await db.Instances.CountAsync(token));
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [service].[FailBatchLogInsert] ON [service].[LogRecords] INSTEAD OF INSERT AS THROW 51007, 'test batch failure', 1", token);
            }
            await Assert.ThrowsAsync<DbUpdateException>(() => export.WriteAsync(source.Id, request, token));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await db.LogRecords.CountAsync(token));
                Assert.AreEqual(0, await db.LogContexts.CountAsync(token));
                Assert.AreEqual(0, await db.Instances.CountAsync(token));
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [service].[FailBatchLogInsert]", token);
            }
            await using (var db = new LogAdmissionDbContext(quotaOptions))
                Assert.AreEqual(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[Buckets] WHERE [Microtokens] = 100000000").SingleAsync(token));
            Assert.AreEqual(ExportStatus.Complete, (await export.WriteAsync(source.Id, request, token)).Status);
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(100, await check.LogRecords.CountAsync(token));
            Assert.AreEqual(1, await check.Instances.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Export_KeepsUnapprovedPayloadOutOfStorageAndReportsPartialSuccess() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var quotaOptions = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "approved", "prod", "Approved", now), now.AddMinutes(-1));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token);
            }
            await using (var db = new LogAdmissionDbContext(quotaOptions)) await db.Database.MigrateAsync(token);
            static ResourceLogs ResourceFor(string name, string body)
            {
                var resource = new ResourceLogs { Resource = new Resource() };
                resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = name } });
                resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
                resource.ScopeLogs.Add(new ScopeLogs { LogRecords = { new OtlpLogRecord { Body = new AnyValue { StringValue = body } } } });
                return resource;
            }
            var request = new ExportLogsServiceRequest();
            request.ResourceLogs.Add(ResourceFor("approved", "accepted"));
            request.ResourceLogs.Add(ResourceFor("pending", "never persist this body"));
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var export = new LogExport(factory, new CandidateDiscovery(factory));
            var result = await export.WriteAsync(source.Id, request, token);
            Assert.AreEqual(ExportStatus.Partial, result.Status);
            Assert.AreEqual(1L, result.Rejected);
            await using var check = new ServiceObservabilityDbContext(options);
            var saved = await check.LogRecords.SingleAsync(token);
            Assert.AreEqual("accepted", OtlpLogRecord.Parser.ParseFrom(saved.RecordPayload).Body.StringValue);
            var candidate = await check.Candidates.SingleAsync(token);
            Assert.AreEqual("pending", candidate.ServiceKey);
            Assert.AreEqual("pending", candidate.DisplayName);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Ingestion_AuthorizesAndCommitsPayloadAndQuotaTogether() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var quotaOptions = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var other = new ServiceSource("other", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now.AddMinutes(-1));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.AddRange(source, other); db.Services.Add(service); await db.SaveChangesAsync(token);
            }
            await using (var db = new LogAdmissionDbContext(quotaOptions)) await db.Database.MigrateAsync(token);
            var resource = new ResourceLogs { Resource = new Resource(), SchemaUrl = "https://example.test/resource" };
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "orders" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            resource.Resource.Attributes.Add(new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "node" } });
            var trace = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            var scope = new ScopeLogs { SchemaUrl = "https://example.test/scope" };
            scope.LogRecords.Add(new OtlpLogRecord { TimeUnixNano = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000,
                ObservedTimeUnixNano = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000,
                SeverityNumber = SeverityNumber.Error, SeverityText = "ERROR",
                TraceId = ByteString.CopyFrom(trace), SpanId = ByteString.CopyFrom(Enumerable.Range(1, 8).Select(i => (byte)i).ToArray()),
                Body = new AnyValue { StringValue = "accepted" } });
            var ingestion = new LogIngestion(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            var oversized = new ScopeLogs();
            for (var i = 0; i < 17; i++)
                oversized.LogRecords.Add(new OtlpLogRecord { Body = new AnyValue { BytesValue = ByteString.CopyFrom(new byte[250_000]) } });
            Assert.AreEqual(LogIngestionStatus.Invalid, (await ingestion.WriteAsync(source.Id, resource, oversized, token)).Status);
            Assert.AreEqual(LogIngestionStatus.Unauthorized, (await ingestion.WriteAsync(other.Id, resource, scope, token)).Status);
            scope.LogRecords[0].TimeUnixNano = (ulong)now.AddMinutes(-2).ToUnixTimeMilliseconds() * 1_000_000;
            Assert.AreEqual(LogIngestionStatus.BeforeApproval, (await ingestion.WriteAsync(source.Id, resource, scope, token)).Status);
            scope.LogRecords[0].TimeUnixNano = 0;
            scope.LogRecords[0].ObservedTimeUnixNano = (ulong)now.AddMinutes(-2).ToUnixTimeMilliseconds() * 1_000_000;
            Assert.AreEqual(LogIngestionStatus.BeforeApproval, (await ingestion.WriteAsync(source.Id, resource, scope, token)).Status);
            scope.LogRecords[0].ObservedTimeUnixNano = (ulong)now.AddHours(1).ToUnixTimeMilliseconds() * 1_000_000;
            Assert.AreEqual(LogIngestionStatus.FutureTimestamp, (await ingestion.WriteAsync(source.Id, resource, scope, token)).Status);
            await using (var check = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await check.LogRecords.CountAsync(token));
                Assert.AreEqual(0, await check.LogContexts.CountAsync(token));
                Assert.AreEqual(0, await check.Instances.CountAsync(token));
                await check.Database.ExecuteSqlRawAsync("CREATE TRIGGER [service].[FailLogInsert] ON [service].[LogRecords] INSTEAD OF INSERT AS THROW 51002, 'test insert failure', 1", token);
            }
            scope.LogRecords[0].TimeUnixNano = 0;
            scope.LogRecords[0].ObservedTimeUnixNano = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000;
            await Assert.ThrowsAsync<DbUpdateException>(() => ingestion.WriteAsync(source.Id, resource, scope, token));
            await using (var check = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await check.LogRecords.CountAsync(token));
                Assert.AreEqual(0, await check.LogContexts.CountAsync(token));
                Assert.AreEqual(0, await check.Instances.CountAsync(token));
                await check.Database.ExecuteSqlRawAsync("DROP TRIGGER [service].[FailLogInsert]", token);
            }
            await using (var quota = new LogAdmissionDbContext(quotaOptions))
                Assert.AreEqual(0, await quota.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[Buckets]").SingleAsync(token));
            Assert.AreEqual(LogIngestionStatus.Accepted, (await ingestion.WriteAsync(source.Id, resource, scope, token)).Status);
            await using (var check = new ServiceObservabilityDbContext(options))
            {
                var saved = await check.LogRecords.Include(x => x.Context).SingleAsync(token);
                Assert.AreEqual(service.Id, saved.ServiceId);
                Assert.IsNotNull(saved.InstanceId);
                Assert.IsFalse(saved.UsedReceivedTime);
                Assert.IsTrue(saved.UsedObservedTime);
                Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()), saved.OccurredAt);
                CollectionAssert.AreEqual(trace, saved.TraceId);
                Assert.AreEqual(resource.SchemaUrl, saved.Context.ResourceSchemaUrl);
                var parsed = OtlpLogRecord.Parser.ParseFrom(saved.RecordPayload);
                Assert.AreEqual("accepted", parsed.Body.StringValue);
                Assert.AreEqual(SeverityNumber.Error, parsed.SeverityNumber);
                Assert.AreEqual("ERROR", parsed.SeverityText);
                Assert.AreEqual(scope.LogRecords[0].ObservedTimeUnixNano, parsed.ObservedTimeUnixNano);
                Assert.AreEqual(scope.LogRecords[0].SpanId, parsed.SpanId);
                var instance = await check.Instances.SingleAsync(x => x.Id == saved.InstanceId, token);
                Assert.AreEqual(7, instance.Id.Version);
                Assert.AreEqual("node", instance.ExternalInstanceId);
                Assert.AreEqual(service.Id, instance.ServiceId);
            }
            await using (var quota = new LogAdmissionDbContext(quotaOptions))
                Assert.AreEqual(2, await quota.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[Buckets]").SingleAsync(token));
            // OTLP carries no reliable universal log identity: equal records are not silently deduplicated.
            Assert.AreEqual(LogIngestionStatus.Accepted, (await ingestion.WriteAsync(source.Id, resource, scope, token)).Status);
            await using (var check = new ServiceObservabilityDbContext(options))
            {
                var identical = await check.LogRecords.AsNoTracking().OrderBy(x => x.RowId).ToArrayAsync(token);
                Assert.HasCount(2, identical);
                CollectionAssert.AreEqual(identical[0].RecordPayload, identical[1].RecordPayload);
            }
            scope.LogRecords[0].ObservedTimeUnixNano = 0;
            Assert.AreEqual(LogIngestionStatus.Accepted, (await ingestion.WriteAsync(source.Id, resource, scope, token)).Status);
            await using (var check = new ServiceObservabilityDbContext(options))
            {
                var fallback = await check.LogRecords.AsNoTracking().OrderByDescending(x => x.RowId).FirstAsync(token);
                Assert.IsTrue(fallback.UsedReceivedTime);
                Assert.IsFalse(fallback.UsedObservedTime);
                Assert.AreEqual(fallback.ReceivedAt, fallback.OccurredAt);
            }
        }, context.CancellationToken);
}
