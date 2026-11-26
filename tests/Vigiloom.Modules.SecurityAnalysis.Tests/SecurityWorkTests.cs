using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Events;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Microsoft.Data.SqlClient;
using Vigiloom.Migrator.Security;
using Vigiloom.Modules.SecurityAnalysis.Domain;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Security;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SecurityWorkTests(TestContext context)
{
    [TestMethod]
    public Task RealDetectionHandlersConsumeHostWorkWithoutActiveRules() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IDbContextFactory<SecurityAnalysisDbContext>>(new Factory(options));
            services.AddSingleton<IHostLogReader, EmptyLogReader>();
            services.AddScoped<ISigmaCatalog, SigmaCatalog>();
            services.AddHostDetection();
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = database.ConnectionString }).Build();
            using var http = new HttpClient(new HostHandler([])) { BaseAddress = new Uri("http://host/") };
            var consumer = new SecurityWorkHandler(config, new TestClients(http), scope.ServiceProvider.GetServices<IOutboxHandler>());
            var dispatch = new HostLogDispatch(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, [Guid.CreateVersion7()]);
            var work = new ServiceWork(HostLogDispatch.CombinedMessageType, JsonSerializer.Serialize(dispatch), Guid.CreateVersion7().ToString());
            await consumer.ExecuteAsync(work, token);
            await consumer.ExecuteAsync(work, token);
        }, context.CancellationToken);

    [TestMethod]
    public void SecurityDetectionHandlersCanBeResolved()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDbContextFactory<SecurityAnalysisDbContext>>(new Factory(new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer("Server=localhost;Database=unused").Options));
        services.AddSingleton<IHostLogReader, EmptyLogReader>();
        services.AddScoped<ISigmaCatalog, SigmaCatalog>();
        services.AddHostDetection();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToArray();
        Assert.IsTrue(handlers.Any(x => x.MessageType == HostLogDispatch.MessageType));
        Assert.IsTrue(handlers.Any(x => x.MessageType == HostLogDispatch.IocMessageType));
    }

    private sealed class EmptyLogReader : IHostLogReader
    {
        public Task<IReadOnlyList<HostLogEntry>> ReadAsync(Guid agentId, IReadOnlyCollection<Guid> messageIds, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<HostLogEntry>>([]);
    }

    [TestMethod]
    public Task HostLogDeliveryRunsBothAnalysersOnce() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = database.ConnectionString }).Build();
            using var http = new HttpClient(new HostHandler([])) { BaseAddress = new Uri("http://host/") };
            var sigma = new RecordingHandler(HostLogDispatch.MessageType);
            var ioc = new RecordingHandler(HostLogDispatch.IocMessageType);
            var consumer = new SecurityWorkHandler(config, new TestClients(http), [sigma, ioc]);
            var dispatch = new HostLogDispatch(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, [Guid.CreateVersion7()]);
            var work = new ServiceWork(HostLogDispatch.CombinedMessageType, JsonSerializer.Serialize(dispatch), Guid.CreateVersion7().ToString());
            await consumer.ExecuteAsync(work, token);
            await consumer.ExecuteAsync(work, token);
            Assert.AreEqual(1, sigma.Calls);
            Assert.AreEqual(1, ioc.Calls);
            var legacyIoc = new ServiceWork(HostLogDispatch.IocMessageType, work.Payload, Guid.CreateVersion7().ToString());
            await consumer.ExecuteAsync(legacyIoc, token);
            Assert.AreEqual(1, sigma.Calls);
            Assert.AreEqual(2, ioc.Calls);
        }, context.CancellationToken);

    private sealed class RecordingHandler(string type) : IOutboxHandler
    {
        public string MessageType => type;
        public int Calls { get; private set; }
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public Task FailedNotificationRollsBackScanChange() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var scans = new YaraScans(new Factory(options), TimeProvider.System, new FailingNotifier());
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scans.CreateAsync(identity,
                new("/tmp/file", "rule test { condition: true }", 1024, 30), Guid.CreateVersion7(), token));
            await using var db = new SecurityAnalysisDbContext(options);
            Assert.AreEqual(0, await db.YaraScans.CountAsync(token));
        }, context.CancellationToken);

    private sealed class FailingNotifier : IYaraScanNotifier
    {
        public Task NotifyAsync(SecurityAnalysisDbContext db, Guid agentId, CancellationToken token) => throw new InvalidOperationException("test failure");
    }

    [TestMethod]
    public Task ScanChangesAndNotificationAreCommittedTogether() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            var notifier = new YaraScanNotifier(new TemporalWorkWriter(new OutboxWriter([new NoopHandler()])), config);
            var timeline = new YaraTimelineWriter(new TemporalWorkWriter(new OutboxWriter([new NoopHandler()])), config);
            var scans = new YaraScans(new Factory(options), TimeProvider.System, notifier, timeline);
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var created = await scans.CreateAsync(identity, new("/tmp/file", "rule test { condition: true }", 1024, 30), Guid.CreateVersion7(), token);
            Assert.IsTrue(await scans.CancelAsync(identity.DeviceId, created.Id, token));
            Assert.IsFalse(await scans.CancelAsync(identity.DeviceId, created.Id, token));
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox WHERE MessageType = 'temporal.service-work' AND Payload LIKE '%YaraScanChanged%'";
            Assert.AreEqual(2, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox WHERE MessageType = 'temporal.service-work' AND Payload LIKE '%event-timeline.append.v1%'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
        }, context.CancellationToken);

    private sealed class NoopHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [TestMethod]
    public Task SecurityOwnedYaraWorkIsScopedToAgentAndCanBeCancelled() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var scans = new YaraScans(new Factory(options), TimeProvider.System);
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var other = new RegisteredAgentIdentity(Guid.CreateVersion7(), identity.DeviceId, 1);
            var created = await scans.CreateAsync(identity, new("/tmp/file", "rule test { condition: true }", 1024, 30), Guid.CreateVersion7(), token);
            Assert.AreEqual(created.Id, (await scans.NextAsync(identity, token))!.Id);
            Assert.IsNull(await scans.NextAsync(other, token));
            Assert.IsTrue(await scans.CancelAsync(identity.DeviceId, created.Id, token));
            Assert.IsNull(await scans.NextAsync(identity, token));
            Assert.AreEqual(YaraScanOutcome.Cancelled, Assert.ContainsSingle(await scans.ListAsync(identity.DeviceId, token)).Result!.Outcome);
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    [TestMethod]
    public Task BaselineResultsAndTimelineAreCommittedTogether() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence.IntelligenceSource { Key = BaselineSources.LinuxShadow };
            var revision = new Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence.IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "1", RetrievedAt = now,
                Digest = new byte[32], Artifact = [1], SchemaVersion = "1", MappingVersion = "1", LicenseEvidence = "test", RecordCount = 1 };
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision);
                db.IntelligenceActivationEvents.Add(new() { SourceId = source.Id, RevisionId = revision.Id, OccurredAt = now, Scheduled = true });
                await db.SaveChangesAsync(token);
            }
            var agent = Guid.CreateVersion7(); var device = Guid.CreateVersion7();
            var batch = new BaselineBatch(BaselineBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
                [new(source.Id, revision.Id, "AAAAAAAAAAA=", BaselineSources.LinuxShadowCheck, now, "debian:12", BaselineOutcome.Pass, "private evidence"),
                 new(source.Id, revision.Id, "AAAAAAAAAAA=", BaselineSources.LinuxShadowCheck, now, "debian:12", BaselineOutcome.Fail, "private evidence")]);
            var host = new HostHandler(BaselineBatchCodec.Serialize(batch));
            using var http = new HttpClient(host) { BaseAddress = new Uri("http://host/") };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = database.ConnectionString }).Build();
            var reference = new HostSecurityBatchReady(agent, batch.BatchId, device, 1, "baseline");
            var work = new ServiceWork(nameof(HostSecurityBatchReady), JsonSerializer.Serialize(reference), Guid.CreateVersion7().ToString());
            var failing = new SecurityWorkHandler(config, new TestClients(http), workWriter: new TemporalWorkWriter(new OutboxWriter([])));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => failing.ExecuteAsync(work, token));
            await using (var db = new SecurityAnalysisDbContext(options)) Assert.AreEqual(0, await db.BaselineObservations.CountAsync(token));
            Assert.AreEqual(0, host.Deletes);
            var consumer = new SecurityWorkHandler(config, new TestClients(http), workWriter: new TemporalWorkWriter(new OutboxWriter([new NoopHandler()])));
            await consumer.ExecuteAsync(work, token);
            await consumer.ExecuteAsync(work, token);
            await using (var db = new SecurityAnalysisDbContext(options)) Assert.AreEqual(2, await db.BaselineObservations.CountAsync(token));
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Payload FROM scheduling.Outbox WHERE MessageType = 'temporal.service-work'";
            var envelope = JsonSerializer.Deserialize<TemporalWorkRequest>((string)(await command.ExecuteScalarAsync(token))!);
            Assert.AreEqual(EventTimelineWork.BatchKind, envelope!.Work.Kind);
            var deliveries = JsonSerializer.Deserialize<EventTimelineBatch>(envelope.Work.Payload)!;
            Assert.HasCount(2, deliveries.Items);
            Assert.AreNotEqual(deliveries.Items[0].MessageId, deliveries.Items[1].MessageId);
            Assert.IsTrue(deliveries.Items.All(x => x.Entry.Source == "baseline" && x.Entry.ResourceId == device && !x.Entry.Evidence.Contains("private evidence")));
            command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox WHERE MessageType = 'temporal.service-work'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
        }, context.CancellationToken);

    [TestMethod]
    public Task DetectionTimelineUsesCallerTransaction() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var config = new ConfigurationBuilder().Build();
            var writer = new DetectionTimelineWriter(new TemporalWorkWriter(new OutboxWriter([new NoopHandler()])), config);
            var device = Guid.CreateVersion7();
            var longKey = new string('測', 2048);
            EventTimelineEntry[] entries = [new("inventory", DateTimeOffset.UtcNow, device, "device", "資產盤點變更：" + longKey, "evidence", $"/devices/{device:D}")];
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                await writer.WriteAsync(db, entries, token);
                await transaction.RollbackAsync(token);
            }
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox";
            Assert.AreEqual(0, (int)(await command.ExecuteScalarAsync(token))!);
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                await writer.WriteAsync(db, entries, token);
                await transaction.CommitAsync(token);
            }
            command.CommandText = "SELECT Payload FROM scheduling.Outbox";
            var envelope = JsonSerializer.Deserialize<TemporalWorkRequest>((string)(await command.ExecuteScalarAsync(token))!);
            Assert.AreEqual(EventTimelineWork.BatchKind, envelope!.Work.Kind);
            var batch = JsonSerializer.Deserialize<EventTimelineBatch>(envelope.Work.Payload)!;
            var entry = Assert.ContainsSingle(batch.Items).Entry;
            Assert.AreEqual(device, entry.ResourceId);
            Assert.AreEqual("資產盤點變更：" + longKey, entry.Summary);
        }, context.CancellationToken);

    [TestMethod]
    public Task RemovedIntegrityScopeDoesNotLeaveARecurringDelivery() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var agent = Guid.CreateVersion7();
            var device = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var item = new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.File, ["test"]), IntegrityItemKind.File, new string('a', 64));
            var measured = IntegrityDigest.MeasureChunk([item]);
            var chunk = new IntegrityChunk(6, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1, Guid.CreateVersion7(), 1, now,
                IntegrityScanOutcome.Complete, false, null, 0, 1, 1, measured.Bytes, IntegrityDigest.Combine([measured.Digest]), [item]);
            var host = new HostHandler(IntegrityChunkCodec.Serialize(chunk));
            using var http = new HttpClient(host) { BaseAddress = new Uri("http://host/") };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = database.ConnectionString }).Build();
            var consumer = new SecurityWorkHandler(config, new TestClients(http));
            var reference = new HostSecurityBatchReady(agent, chunk.BatchId, device, 1, "integrity");
            var work = new ServiceWork(nameof(HostSecurityBatchReady), JsonSerializer.Serialize(reference), Guid.CreateVersion7().ToString());
            await consumer.ExecuteAsync(work, token);
            await consumer.ExecuteAsync(work, token);
            Assert.AreEqual(1, host.Gets);
            Assert.AreEqual(2, host.Deletes);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            Assert.AreEqual(0, await db.IntegrityScans.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task OutOfOrderAndConflictingInventoryKeepsCommittedStaging() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var agent = Guid.CreateVersion7();
            var device = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            InventoryEntry[] items = [new("a", "a", "1", "x64")];
            var measured = InventoryDigest.MeasureChunk(items);
            var active = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 2,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 2, 2, measured.Bytes * 2L,
                InventoryDigest.Combine([measured.Digest, measured.Digest]), items);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = database.ConnectionString }).Build();
            async Task Deliver(InventoryChunk chunk)
            {
                var host = new HostHandler(InventoryChunkCodec.Serialize(chunk));
                using var http = new HttpClient(host) { BaseAddress = new Uri("http://host/") };
                var consumer = new SecurityWorkHandler(config, new TestClients(http));
                var reference = new HostSecurityBatchReady(agent, chunk.BatchId, device, 1, "inventory");
                var work = new ServiceWork(nameof(HostSecurityBatchReady), JsonSerializer.Serialize(reference), Guid.CreateVersion7().ToString());
                await consumer.ExecuteAsync(work, token);
                Assert.AreEqual(1, host.Deletes);
            }
            await Deliver(active);
            await Deliver(active with { Generation = 3, SnapshotId = Guid.CreateVersion7(), BatchId = Guid.CreateVersion7(), TotalBytes = 4 });
            await Deliver(active with { Generation = 1, SnapshotId = Guid.CreateVersion7(), BatchId = Guid.CreateVersion7() });
            await using var db = new SecurityAnalysisDbContext(options);
            var state = await db.InventorySources.SingleAsync(token);
            Assert.AreEqual(2L, state.ActiveGeneration);
            Assert.AreEqual(0L, state.ClosedGeneration);
            Assert.AreEqual(1, await db.InventoryChunks.CountAsync(token));
            Assert.AreEqual(1, await db.InventorySnapshots.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task SavesYaraResultBeforeDeletingHostBatchAndDeduplicatesRetry() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var agent = Guid.CreateVersion7();
            var device = Guid.CreateVersion7();
            var id = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                db.YaraScans.Add(new YaraScan { Id = id, AgentId = agent, DeviceId = device, RequestedBy = Guid.CreateVersion7(),
                    FilePath = "/tmp/test", Rules = "rule test { condition: true }", MaximumBytes = 1024, TimeoutSeconds = 30, RequestedAt = now });
                await db.SaveChangesAsync(token);
            }
            var payload = YaraResultBatchCodec.Serialize(new(YaraResultBatchCodec.SchemaVersion, id, now,
                new(YaraScanOutcome.Completed, "matched", "", false, now)));
            var fake = new HostHandler(payload);
            using var http = new HttpClient(fake) { BaseAddress = new Uri("http://host/") };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = database.ConnectionString }).Build();
            var consumer = new SecurityWorkHandler(config, new TestClients(http), timeline: new YaraTimelineWriter(new TemporalWorkWriter(new OutboxWriter([new NoopHandler()])), config));
            var reference = new HostSecurityBatchReady(agent, id, device, 1, "yara");
            var work = new ServiceWork(nameof(HostSecurityBatchReady), JsonSerializer.Serialize(reference), Guid.CreateVersion7().ToString());
            var failing = new SecurityWorkHandler(config, new TestClients(http), timeline: new YaraTimelineWriter(new TemporalWorkWriter(new OutboxWriter([])), config));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => failing.ExecuteAsync(work, token));
            await using (var beforeRetry = new SecurityAnalysisDbContext(options))
                Assert.IsNull((await beforeRetry.YaraScans.SingleAsync(token)).Outcome);
            Assert.AreEqual(0, fake.Deletes);
            await consumer.ExecuteAsync(work, token);
            await consumer.ExecuteAsync(work, token);
            await using var verify = new SecurityAnalysisDbContext(options);
            var scan = await verify.YaraScans.SingleAsync(token);
            Assert.AreEqual("matched", scan.Output);
            Assert.AreEqual((int)YaraScanOutcome.Completed, scan.Outcome);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Payload FROM scheduling.Outbox WHERE MessageType = 'temporal.service-work'";
            var envelope = JsonSerializer.Deserialize<TemporalWorkRequest>((string)(await command.ExecuteScalarAsync(token))!);
            Assert.IsNotNull(envelope);
            Assert.AreEqual(Vigiloom.Contracts.Events.EventTimelineWork.Kind, envelope.Work.Kind);
            var timeline = JsonSerializer.Deserialize<Vigiloom.Contracts.Events.EventTimelineDelivery>(envelope.Work.Payload);
            Assert.AreEqual(id, timeline!.MessageId);
            Assert.AreEqual(device, timeline.Entry.ResourceId);
            Assert.AreEqual("yara", timeline.Entry.Source);
            command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox WHERE MessageType = 'temporal.service-work'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            Assert.AreEqual(2, fake.Gets);
            Assert.AreEqual(2, fake.Deletes);
        }, context.CancellationToken);

    private sealed class TestClients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class HostHandler(byte[] payload) : HttpMessageHandler
    {
        public int Gets { get; private set; }
        public int Deletes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) { Gets++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }); }
            Deletes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
