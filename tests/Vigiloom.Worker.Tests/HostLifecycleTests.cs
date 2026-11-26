using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Hangfire;
using Microsoft.Data.SqlClient;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Worker.Hosting;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Contracts.Agent;
using System.Security.Cryptography;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

namespace Vigiloom.Worker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HostLifecycleTests(TestContext testContext)
{
    [TestMethod]
    public void HostCutoverRetiresLegacyMetricSchedules()
    {
        var builder = WorkerHost.CreateBuilder(["--Services:HostMonitoring:Url=http://host"]);
        var hosted = builder.Services.Where(service => service.ServiceType == typeof(IHostedService))
            .Select(service => service.ImplementationType?.Name).ToArray();
        Assert.DoesNotContain("HostMetricRollupSchedule", hosted);
        Assert.DoesNotContain("HostMetricRetentionSchedule", hosted);
        Assert.Contains("RetiredHostMetricSchedule", hosted);
    }

    [TestMethod]
    public void NetworkCutoverStopsLegacyPingAndSpeedWorkers()
    {
        var builder = WorkerHost.CreateBuilder(["--Services:Network:Url=http://network"]);
        var hosted = builder.Services.Where(service => service.ServiceType == typeof(IHostedService))
            .Select(service => service.ImplementationType?.Name).ToArray();
        foreach (var name in new[] { "PingWorker", "SpeedWorker", "SpeedSchedule", "SpeedRetentionSchedule", "PingRollupBackfillSchedule", "PingRetentionSchedule" })
            Assert.DoesNotContain(name, hosted);
        Assert.Contains("HealthWorker", hosted);
    }

    [TestMethod]
    public void ObservabilityCutoverStopsLegacyApplicationJobs()
    {
        var builder = WorkerHost.CreateBuilder(["--Services:Observability:Url=http://observability"]);
        var hosted = builder.Services.Where(service => service.ServiceType == typeof(IHostedService))
            .Select(service => service.ImplementationType?.Name).ToArray();
        foreach (var name in new[] { "HealthWorker", "HealthPolicyWorker", "HealthExpiryWorker", "TelemetryRetentionSchedule", "MetricRollupSchedule" })
            Assert.DoesNotContain(name, hosted);
    }

    [TestMethod]
    public async Task MsrcNamedHttpClient_DoesNotFollowRedirect()
    {
        var builder = WorkerHost.CreateBuilder([]);
        using var host = builder.Build();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = socket.GetStream();
            var buffer = new byte[2048];
            Assert.IsGreaterThan(0, await stream.ReadAsync(buffer, deadline.Token));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
        }, deadline.Token);
        var failures = new List<Exception>();
        try
        {
            using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(IntelligenceHttp.MsrcClientName);
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/original", deadline.Token);
            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
            await server;
            Assert.IsFalse(listener.Pending());
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { await deadline.CancelAsync(); } catch (Exception error) { failures.Add(error); }
            try { listener.Stop(); } catch (Exception error) { failures.Add(error); }
            try { await server; } catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (SocketException) when (deadline.IsCancellationRequested) { } catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Database")]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task InboxRetentionJob_CleansRealReceiptAndPreservesReplayFence() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        GlobalConfiguration.Configuration.UseNoOpLogProvider();
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString });
        builder.Services.AddSingleton<ISpeedProbe>(new FixtureSpeedProbe());
        using var host = builder.Build();
        var services = host.Services;
        var resources = services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var agents = services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
        var identityDb = services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
        await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await identityDb.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        var securityDb = services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>();
        await using (var db = await securityDb.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        var initialAdminId = Guid.CreateVersion7();
        await using (var db = await identityDb.CreateDbContextAsync(token))
        {
            db.AccountAuditEvents.Add(new AccountAuditEvent(null, initialAdminId, "InitialAdministratorCreated", old.AddDays(-400)));
            db.AccountAuditEvents.Add(new AccountAuditEvent(initialAdminId, initialAdminId, "AccountRenamed", old.AddDays(-400)));
            db.AccountAuditEvents.Add(new AccountAuditEvent(initialAdminId, initialAdminId, "PasswordChanged", old));
            db.ApiCredentialAuditEvents.Add(new ApiCredentialAuditEvent(initialAdminId, Guid.CreateVersion7(), "ApiCredentialIssued", old.AddDays(-400)));
            db.ApiCredentialAuditEvents.Add(new ApiCredentialAuditEvent(initialAdminId, Guid.CreateVersion7(), "ApiCredentialRevoked", old));
            await db.SaveChangesAsync(token);
        }
        var device = new Device("inbox worker", "127.0.0.1", true, old);
        await using (var db = await resources.CreateDbContextAsync(token))
        {
            db.Devices.Add(device);
            db.DeviceAudits.Add(new DeviceAudit(device.Id, Guid.CreateVersion7(), "updated", old.AddDays(-400)));
            db.DeviceAudits.Add(new DeviceAudit(device.Id, Guid.CreateVersion7(), "created", old));
            await db.SaveChangesAsync(token);
        }
        var network = services.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        var operations = services.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
        var auditActor = Guid.CreateVersion7();
        var auditTarget = Guid.CreateVersion7();
        var schedulingActor = Guid.CreateVersion7();
        var completedWork = Guid.CreateVersion7();
        var openAlert = Guid.CreateVersion7(); var closedAlert = Guid.CreateVersion7(); var oldNotification = Guid.CreateVersion7();
        await using (var db = await operations.CreateDbContextAsync(token))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.AuditEvents (ActorId, ResourceId, OccurredAt, Kind, Details) VALUES ({auditActor}, {auditTarget}, {old.AddDays(-400)}, {1}, {"{}"})", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.PingAlerts (Id, DeviceId, Target, OpenedAt, RecoveredAt) VALUES ({openAlert}, {device.Id}, {"open"}, {old.AddDays(-400)}, {null}), ({closedAlert}, {device.Id}, {"closed"}, {old.AddDays(-400)}, {old.AddDays(-400)})", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.Notifications (Id, MessageId, Source, OccurredAt, Title, Detail) VALUES ({oldNotification}, {Guid.CreateVersion7()}, {"ping"}, {old.AddDays(-400)}, {"title"}, {"detail"})", token);
        }
        await using (var db = await services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [scheduling].[DashboardAudit] ([ActorId], [OccurredAt], [Action], [Result]) VALUES ({schedulingActor}, {old.AddDays(-400)}, '/jobs/enqueued/delete', 'accepted'), ({schedulingActor}, {old.AddDays(-400)}, '/jobs/enqueued/delete', 'started'), ({schedulingActor}, {old}, '/jobs/enqueued/delete', 'accepted')", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [scheduling].[Outbox] ([MessageId], [MessageType], [Payload], [CreatedAt], [AvailableAt], [State], [Attempts], [CompletedAt]) VALUES ({completedWork}, {"agent.host-log.received.v1"}, {"{}"}, {old.AddDays(-400)}, {old.AddDays(-400)}, {2}, {1}, {old.AddDays(-400)})", token);
        }
        var oldPing = new PingSample(device.Id, "central", old.AddDays(-30), old.AddDays(-30), true, 1);
        await using (var db = await network.CreateDbContextAsync(token)) { db.PingSamples.Add(oldPing); await db.SaveChangesAsync(token); }
        Assert.AreEqual(1, await new PingRollupBackfill(network).RunBatchAsync(token));
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), TimeProvider.System);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var agentId = registered.AgentId!.Value;
        await using (var db = await agents.CreateDbContextAsync(token))
        {
            var settings = new AgentSetting { DeviceId = device.Id, UpdatedBy = auditActor, UpdatedAt = old };
            var queue = new AgentQueueCapacitySetting { DeviceId = device.Id, UpdatedBy = auditActor, UpdatedAt = old };
            var metrics = new AgentMetricSetting { DeviceId = device.Id, UpdatedBy = auditActor, UpdatedAt = old };
            var logSource = new HostLogSource(device.Id, AgentLogSourceKind.WindowsEvent, "System", "System", true, null, auditActor, old.AddDays(-400));
            var threshold = new HostMetricThreshold { DeviceId = device.Id, AgentId = agentId, Kind = AgentMetricKind.MemoryAvailableBytes,
                Source = "system", SeriesHash = new byte[32], Enabled = true, Comparison = HostMetricComparison.AtLeast, Value = 1, FreshnessSeconds = 30 };
            db.Settings.Add(settings); db.QueueCapacitySettings.Add(queue); db.MetricSettings.Add(metrics);
            db.HostLogSources.Add(logSource); db.HostMetricThresholds.Add(threshold);
            await db.SaveChangesAsync(token);
            db.HostLogSourceAudits.Add(new HostLogSourceAudit(logSource, HostLogSourceAction.Created));
            logSource.Update("System updated", true, null, auditActor, old);
            await db.SaveChangesAsync(token);
            db.HostLogSourceAudits.Add(new HostLogSourceAudit(logSource, HostLogSourceAction.Updated));
            foreach (var time in new[] { old.AddDays(-400), old })
            {
                db.Audits.Add(new AgentAudit(device.Id, agentId, auditActor, "AgentEnrolled", time));
                db.SettingAudits.Add(new AgentSettingAudit { SettingId = settings.Id, DeviceId = device.Id, ActorId = auditActor, OccurredAt = time,
                    EffectiveRevision = "test", ChangeVersion = new byte[8] });
                db.QueueCapacityAudits.Add(new AgentQueueCapacityAudit { SettingId = queue.Id, DeviceId = device.Id, ActorId = auditActor, OccurredAt = time,
                    EffectiveRevision = "test", ChangeVersion = new byte[8] });
                db.MetricSettingAudits.Add(new AgentMetricSettingAudit { SettingId = metrics.Id, DeviceId = device.Id, ActorId = auditActor, OccurredAt = time,
                    EffectiveRevision = "test", ChangeVersion = new byte[8] });
                db.HostMetricThresholdAudits.Add(new HostMetricThresholdAudit { ThresholdId = threshold.Id, DeviceId = device.Id, ActorId = auditActor,
                    OccurredAt = time, Enabled = true, Comparison = HostMetricComparison.AtLeast, Value = 1, FreshnessSeconds = 30, Version = new byte[8] });
            }
            var point = new AgentMetricPoint(Guid.CreateVersion7(), old, AgentMetricKind.MemoryAvailableBytes, "system", null, 42);
            db.HostMetrics.Add(new HostMetric(agentId, Guid.CreateVersion7(), 0, point, old));
            var expired = point with { MessageId = Guid.CreateVersion7(), OccurredAt = old.AddDays(-31), Dimension = "old" };
            db.HostMetrics.Add(new HostMetric(agentId, Guid.CreateVersion7(), 0, expired, old));
            var logBatch = new AgentLogBatch(3, Guid.CreateVersion7(), old, Guid.CreateVersion7(), AgentLogSourceKind.Journal,
                "system", 1, [new(Guid.CreateVersion7(), old, "expired", null, null, null, null, null),
                    new(Guid.CreateVersion7(), old, "recent", null, null, null, null, null)]);
            db.HostLogs.Add(new HostLog(agentId, logBatch, 0, old.AddDays(-20)));
            db.HostLogs.Add(new HostLog(agentId, logBatch, 1, old));
            await db.SaveChangesAsync(token);
        }
        await using (var db = await securityDb.CreateDbContextAsync(token))
        {
            var inventory = new InventorySetting { DeviceId = device.Id, UpdatedBy = auditActor, UpdatedAt = old };
            var integrity = new IntegrityScopeSetting { DeviceId = device.Id, UpdatedBy = auditActor, UpdatedAt = old, Revision = 1, DefinitionJson = "{}" };
            db.InventorySettings.Add(inventory); db.IntegrityScopes.Add(integrity);
            foreach (var time in new[] { old.AddDays(-400), old })
            {
                db.InventorySettingAudits.Add(new InventorySettingAudit { SettingId = inventory.Id, DeviceId = device.Id, ActorId = auditActor,
                    OccurredAt = time, EffectiveScanIntervalSeconds = 3600, EffectiveRevision = "test", ChangeVersion = new byte[8] });
                db.IntegrityScopeAudits.Add(new IntegrityScopeAudit { ScopeId = integrity.Id, DeviceId = device.Id, ActorId = auditActor,
                    OccurredAt = time, Revision = 1, Action = IntegrityScopeAction.Created, Enabled = true });
            }
            await db.SaveChangesAsync(token);
        }
        var serviceDb = services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        await using (var db = await serviceDb.CreateDbContextAsync(token))
        {
            var source = new ServiceSource("telemetry worker", new byte[32], old.AddDays(-30));
            var service = new MonitoredService(new ServiceCandidate(source.Id, "worker", "test", "Worker", old.AddDays(-30)), old.AddDays(-30));
            db.Sources.Add(source); db.Services.Add(service);
            db.LogRecords.Add(new LogRecord { ServiceId = service.Id, Context = new LogContext { ServiceId = service.Id,
                ResourcePayload = [], ScopePayload = [], ResourceSchemaUrl = "", ScopeSchemaUrl = "" },
                OccurredAt = old.AddDays(-10), ReceivedAt = old, RecordPayload = [] });
            db.TraceSpans.Add(new TraceSpan { ServiceId = service.Id, Context = new TraceContext { ServiceId = service.Id,
                ResourcePayload = [], ScopePayload = [], ResourceSchemaUrl = "", ScopeSchemaUrl = "" },
                StartedAt = old, EndedAt = old, ReceivedAt = old, TraceId = new byte[16], SpanId = new byte[8], SpanPayload = [] });
            await db.SaveChangesAsync(token);
        }
        var credential = $"vla_{agentId:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        var message = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, old, "test", "X64", ".NET 10");
        var payload = AgentBatchCodec.Serialize(new AgentBatch(1, Guid.CreateVersion7(), old, [message]));
        var receiver = new AgentBatchReceiver(agents, identity, TimeProvider.System);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, payload, token));
        await using (var db = await agents.CreateDbContextAsync(token))
        {
            await db.Database.ExecuteSqlRawAsync("DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'); UPDATE [agent].[ReplayPolicies] SET [Phase] = 2, [FreshInstallation] = 1, [ActivatedAt] = DATEADD(day, -10, @now), [EnforcedAt] = DATEADD(day, -10, @now), [CleanupCutoverAt] = DATEADD(day, -9, @now), [InboxSettingsChangedAt] = DATEADD(day, -9, @now), [LegacyReceiptsRetainUntil] = DATEADD(day, -2, @now) WHERE [Scope] = 1", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[ReceivedBatches] SET [RetainUntil] = DATEADD(day, -1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [AgentId] = {agentId}", token);
        }
        await host.StartAsync(token);
        try
        {
            using var scope = services.CreateScope();
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<AdvisoryRefreshJob>());
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<SigmaRefreshJob>());
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<SignatureRefreshJob>());
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<UbuntuRefreshJob>());
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<MsrcRefreshJob>());
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<EpssRefreshJob>());
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<LinuxReassessmentJob>());
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<WindowsReassessmentJob>());
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<MetricRollupJob>());
            Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<AuditRetentionJob>());
            var jobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            var id = jobs.Enqueue<AgentInboxRetentionJob>(job => job.RunAsync(CancellationToken.None));
            var pingRetentionId = jobs.Enqueue<PingRetentionJob>(job => job.RunAsync(CancellationToken.None));
            var auditRetentionId = jobs.Enqueue<AuditRetentionJob>(job => job.RunAsync(CancellationToken.None));
            var hostRollupId = jobs.Enqueue<HostMetricRollupJob>(job => job.RunAsync(CancellationToken.None));
            using var connection = scope.ServiceProvider.GetRequiredService<JobStorage>().GetConnection();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                var state = connection.GetStateData(id)?.Name;
                var pingState = connection.GetStateData(pingRetentionId)?.Name;
                var auditState = connection.GetStateData(auditRetentionId)?.Name;
                var hostRollupState = connection.GetStateData(hostRollupId)?.Name;
                Assert.AreNotEqual("Failed", state);
                Assert.AreNotEqual("Failed", pingState);
                Assert.AreNotEqual("Failed", auditState);
                Assert.AreNotEqual("Failed", hostRollupState);
                if (state == "Succeeded" && pingState == "Succeeded" && auditState == "Succeeded" && hostRollupState == "Succeeded") break;
                await Task.Delay(100, deadline.Token);
            }
            var hostRetentionId = jobs.Enqueue<HostMetricRetentionJob>(job => job.RunAsync(CancellationToken.None));
            var telemetryRetentionId = jobs.Enqueue<TelemetryRetentionJob>(job => job.RunAsync(CancellationToken.None));
            while (true)
            {
                var state = connection.GetStateData(hostRetentionId)?.Name;
                var telemetryState = connection.GetStateData(telemetryRetentionId)?.Name;
                Assert.AreNotEqual("Failed", state);
                Assert.AreNotEqual("Failed", telemetryState);
                if (state == "Succeeded" && telemetryState == "Succeeded") break;
                await Task.Delay(100, deadline.Token);
            }
            await using var check = await agents.CreateDbContextAsync(token);
            Assert.AreEqual(0, await check.ReceivedBatches.CountAsync(token));
            Assert.AreEqual(1, await check.RuntimeObservations.CountAsync(token));
            Assert.AreEqual("recent", (await check.HostLogs.SingleAsync(token)).Content);
            Assert.AreEqual(42d, (await check.HostMetricRollups.SingleAsync(x => x.Dimension == null, token)).Sum);
            Assert.AreEqual(0, await check.HostMetrics.CountAsync(x => x.Dimension == "old", token));
            Assert.AreEqual(1, await check.HostMetricRollups.CountAsync(x => x.Dimension == "old", token));
            Assert.AreEqual(1, await check.Audits.CountAsync(x => x.ActorId == auditActor, token));
            Assert.AreEqual(1, await check.HostLogSourceAudits.CountAsync(x => x.ActorId == auditActor, token));
            Assert.AreEqual(1, await check.SettingAudits.CountAsync(x => x.ActorId == auditActor, token));
            Assert.AreEqual(1, await check.QueueCapacityAudits.CountAsync(x => x.ActorId == auditActor, token));
            Assert.AreEqual(1, await check.MetricSettingAudits.CountAsync(x => x.ActorId == auditActor, token));
            Assert.AreEqual(1, await check.HostMetricThresholdAudits.CountAsync(x => x.ActorId == auditActor, token));
            await using (var checkSecurity = await securityDb.CreateDbContextAsync(token))
            {
                Assert.AreEqual(1, await checkSecurity.InventorySettingAudits.CountAsync(x => x.ActorId == auditActor, token));
                Assert.AreEqual(1, await checkSecurity.IntegrityScopeAudits.CountAsync(x => x.ActorId == auditActor, token));
            }
            await using (var telemetry = await serviceDb.CreateDbContextAsync(token))
            {
                Assert.AreEqual(0, await telemetry.LogRecords.CountAsync(token));
                Assert.AreEqual(0, await telemetry.TraceSpans.CountAsync(token));
                Assert.AreEqual(0, await telemetry.LogContexts.CountAsync(token));
                Assert.AreEqual(0, await telemetry.TraceContexts.CountAsync(token));
            }
            var agent = await check.Agents.SingleAsync(x => x.Id == agentId, token);
            Assert.AreEqual(1L, agent.InboxDeletedTotal);
            Assert.IsNotNull(agent.ReplayCleanedBefore);
            Assert.AreEqual(BatchReceiptStatus.Expired, await receiver.ReceiveAsync(credential, payload, token));
            await using (var checkNetwork = await network.CreateDbContextAsync(token))
                Assert.IsFalse(await checkNetwork.PingSamples.AnyAsync(x => x.RowId == oldPing.RowId, token));
            await using (var checkOperations = await operations.CreateDbContextAsync(token))
            {
                Assert.AreEqual(0, await checkOperations.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.AuditEvents WHERE ResourceId = {auditTarget}").SingleAsync(token));
                Assert.AreEqual(1, await checkOperations.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.PingAlerts WHERE Id = {openAlert}").SingleAsync(token));
                Assert.AreEqual(0, await checkOperations.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.PingAlerts WHERE Id = {closedAlert}").SingleAsync(token));
                Assert.AreEqual(0, await checkOperations.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.Notifications WHERE Id = {oldNotification}").SingleAsync(token));
            }
            await using (var checkScheduling = await services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-365);
                Assert.AreEqual(0, await checkScheduling.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [scheduling].[Outbox] WHERE [MessageId] = {completedWork}").SingleAsync(token));
                Assert.AreEqual(0, await checkScheduling.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [scheduling].[DashboardAudit] WHERE [ActorId] = {schedulingActor} AND [Result] = 'accepted' AND [OccurredAt] < {cutoff}").SingleAsync(token));
                Assert.AreEqual(1, await checkScheduling.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [scheduling].[DashboardAudit] WHERE [ActorId] = {schedulingActor} AND [Result] = 'started'").SingleAsync(token));
                Assert.AreEqual(1, await checkScheduling.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [scheduling].[DashboardAudit] WHERE [ActorId] = {schedulingActor} AND [Result] = 'accepted' AND [OccurredAt] >= {cutoff}").SingleAsync(token));
            }
            await using (var checkIdentity = await identityDb.CreateDbContextAsync(token))
            {
                Assert.IsTrue(await checkIdentity.AccountAuditEvents.AnyAsync(x => x.SubjectId == initialAdminId && x.Action == "InitialAdministratorCreated", token));
                Assert.IsFalse(await checkIdentity.AccountAuditEvents.AnyAsync(x => x.Action == "AccountRenamed", token));
                Assert.IsTrue(await checkIdentity.AccountAuditEvents.AnyAsync(x => x.Action == "PasswordChanged", token));
                Assert.IsFalse(await checkIdentity.ApiCredentialAuditEvents.AnyAsync(x => x.Action == "ApiCredentialIssued", token));
                Assert.IsTrue(await checkIdentity.ApiCredentialAuditEvents.AnyAsync(x => x.Action == "ApiCredentialRevoked", token));
            }
            await using (var checkResources = await resources.CreateDbContextAsync(token))
            {
                Assert.IsFalse(await checkResources.DeviceAudits.AnyAsync(x => x.DeviceId == device.Id && x.Action == "updated", token));
                Assert.IsTrue(await checkResources.DeviceAudits.AnyAsync(x => x.DeviceId == device.Id && x.Action == "created", token));
            }
            await using var sql = new SqlConnection(database.ConnectionString);
            await sql.OpenAsync(token);
            await using var command = sql.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-agent-inbox-retention'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-metric-rollup'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-ping-retention'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-settings-audit-retention'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-host-metric-rollup'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-host-metric-retention'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-telemetry-retention'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-advisory-refresh'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-sigma-refresh'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-signature-refresh'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-ubuntu-osv-refresh'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-linux-reassessment'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-windows-reassessment'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
        }
        finally { await host.StopAsync(CancellationToken.None); }
    }, testContext.CancellationToken);

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Database")]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Host_CollectsHealth_CancelsOnShutdown_AndRecoversAfterLeaseExpiry() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        GlobalConfiguration.Configuration.UseNoOpLogProvider();
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var scheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        var network = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
        var serviceOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
        await using (var db = new SchedulingDbContext(scheduling)) await db.Database.MigrateAsync(token);
        await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
        await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(token);
        await using (var db = new NetworkDbContext(network)) await db.Database.MigrateAsync(token);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        var receivedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedThird = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? server = null;
        IHost? host = null;
        Exception? failure = null;
        try
        {
            var now = DateTimeOffset.UtcNow.AddSeconds(-1);
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            var target = new HealthTarget(service.Id, null, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/health", 60, 30, 3, 2, now);
            target.SetEnabled(true, now);
            var staleTarget = new HealthTarget(service.Id, null, "http://127.0.0.1:1/health", 30, 5, 3, 2, now);
            staleTarget.SetEnabled(true, now);
            var staleEvidence = now.AddSeconds(-70);
            await using (var db = new ServiceObservabilityDbContext(serviceOptions))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.Add(service); db.HealthTargets.AddRange(target, staleTarget);
                db.HealthSchedules.Add(new HealthSchedule(target.Id, true, now));
                db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, 0, 0, now));
                var staleSchedule = new HealthSchedule(staleTarget.Id, true, now.AddHours(1));
                staleSchedule.SetEvaluation(1, staleEvidence, 1, 2, staleEvidence, staleEvidence);
                db.HealthSchedules.Add(staleSchedule);
                db.HealthStateIntervals.Add(new HealthStateInterval(staleTarget.Id, 1, 1, staleEvidence));
                var policy = new HealthNetworkPolicy(["127.0.0.0/8"], now);
                db.HealthNetworkPolicies.Add(policy);
                await db.SaveChangesAsync(token);
                staleSchedule.SetVersions(source.Version, service.Version, null, staleTarget.Version, policy.Version);
                await db.SaveChangesAsync(token);
            }
            server = ServeHealthAsync(listener, receivedSecond, receivedThird, deadline.Token);
            var builder = WorkerHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString });
            host = builder.Build();
            await host.StartAsync(deadline.Token);
            while (true)
            {
                await using var db = new ServiceObservabilityDbContext(serviceOptions);
                if (await db.HealthObservations.AnyAsync(deadline.Token))
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [DueAt] = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [TargetId] = {target.Id}", deadline.Token);
                    break;
                }
                await Task.Delay(100, deadline.Token);
            }
            await receivedSecond.Task.WaitAsync(deadline.Token);
            // Include the policy transaction in the timeout that excludes the normal 30-second probe timeout.
            var policyRetry = receivedThird.Task.WaitAsync(TimeSpan.FromSeconds(15), deadline.Token);
            await using (var db = new ServiceObservabilityDbContext(serviceOptions))
            {
                var policy = await db.HealthNetworkPolicies.SingleAsync(token);
                var changedAt = DateTimeOffset.UtcNow;
                policy.ReplaceNetworks(["127.0.0.0/8"], changedAt);
                db.HealthNetworkRevisions.Add(new HealthNetworkRevision(policy, Guid.CreateVersion7(), changedAt));
                await db.SaveChangesAsync(token);
            }
            await policyRetry;
            await host.StopAsync(deadline.Token);
            host.Dispose(); host = null;
            long previousGeneration;
            await using (var verify = new ServiceObservabilityDbContext(serviceOptions))
            {
                Assert.AreEqual(1, await verify.HealthObservations.CountAsync(token));
                Assert.AreEqual((byte)0, (await verify.HealthSchedules.SingleAsync(x => x.TargetId == staleTarget.Id, token)).State);
                var interrupted = await verify.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token);
                Assert.IsNotNull(interrupted.LeaseOwner);
                previousGeneration = interrupted.Generation;
                // Simulate passage of the durable lease after an interrupted worker.
                await verify.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [LeaseUntil] = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')), [DueAt] = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [TargetId] = {target.Id}", token);
            }
            var restarted = WorkerHost.CreateBuilder([]);
            restarted.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString });
            host = restarted.Build();
            await host.StartAsync(deadline.Token);
            while (true)
            {
                await using var db = new ServiceObservabilityDbContext(serviceOptions);
                if (await db.HealthObservations.CountAsync(deadline.Token) == 2) break;
                await Task.Delay(100, deadline.Token);
            }
            await server.WaitAsync(deadline.Token);
            await using (var verify = new ServiceObservabilityDbContext(serviceOptions))
            {
                var recovered = await verify.HealthObservations.OrderBy(x => x.RowId).LastAsync(token);
                Assert.AreEqual(target.Id, recovered.TargetId);
                Assert.AreEqual(HealthObservationKind.Healthy, recovered.Kind);
                Assert.IsGreaterThan(previousGeneration, recovered.LeaseGeneration);
            }
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            deadline.Cancel(); listener.Stop();
            try
            {
                if (host is not null)
                {
                    try { await StopForCleanupAsync(host, failure); }
                    finally { host.Dispose(); }
                }
            }
            finally
            {
                if (server is not null) try { await server; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            }
        }
    }, testContext.CancellationToken);

    private static async Task ServeHealthAsync(TcpListener listener, TaskCompletionSource receivedSecond, TaskCompletionSource receivedThird, CancellationToken token)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
            if (attempt is 0 or 3)
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: close\r\n\r\nHealthy"u8.ToArray(), token);
            else
            {
                if (attempt == 1) receivedSecond.TrySetResult(); else receivedThird.TrySetResult();
                try { Assert.AreEqual(0, await stream.ReadAsync(new byte[1], token)); }
                catch (IOException) { /* A reset also proves that the client closed the connection. */ }
            }
        }
    }

    [TestMethod]
    public void SpeedtestHttpHandlers_ArePooledAcrossMeasurements()
    {
        using var host = WorkerHost.CreateBuilder([]).Build();
        var factory = host.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        using var first = factory.CreateHandler(SpeedtestNetProbe.HttpClientName);
        using var second = factory.CreateHandler(SpeedtestNetProbe.HttpClientName);
        Assert.AreSame(first, second);
    }
    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Host_WithoutDatabaseConfiguration_FailsInsteadOfSilentlyStarting()
    {
        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = null });
        using var host = builder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(testContext.CancellationToken));
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Database")]
    [Timeout(150000, CooperativeCancellation = true)]
    public Task Host_ScannerFailure_IsPersistedWithoutStoppingWorker() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        GlobalConfiguration.Configuration.UseNoOpLogProvider();
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var scheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new SchedulingDbContext(scheduling)) await db.Database.MigrateAsync(token);
        var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
        var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(token);
        // Omit the network schema: the real scanner must fail without terminating the host.
        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString });
        using var host = builder.Build();
        Guid originalBoot = Guid.Empty;
        Exception? firstFailure = null;
        try
        {
            await host.StartAsync(token);
            using var scope = host.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<WorkerDiagnosticsStore>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            while (true)
            {
                var workers = await store.GetAsync(deadline.Token);
                var failed = workers.SingleOrDefault(worker => worker.Observation.Ping.ConsecutiveFailures > 0 && worker.Observation.Speed.ConsecutiveFailures > 0);
                if (failed is not null) { originalBoot = failed.BootId; break; }
                await Task.Delay(250, deadline.Token);
            }
            Assert.IsFalse(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        }
        catch (Exception exception) { firstFailure = exception; throw; }
        finally { await StopForCleanupAsync(host, firstFailure); }
        await using (var db = new OperationsDbContext(operations))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE operations.WorkerDiagnosticSlots SET LeaseUntil = DATEADD(second, -1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE BootId = {originalBoot}", token);
        var restartedBuilder = WorkerHost.CreateBuilder([]);
        restartedBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString });
        using var restarted = restartedBuilder.Build();
        Exception? restartFailure = null;
        try
        {
            await restarted.StartAsync(token);
            using var scope = restarted.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<WorkerDiagnosticsStore>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Allow the immediate attempt and multiple 15-second publisher retries to finish.
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            WorkerDiagnosticsSnapshot? latest = null;
            try
            {
                while (true)
                {
                    latest = await store.GetSnapshotAsync(deadline.Token);
                    if (latest.Workers.Any(worker => worker.BootId != originalBoot && worker.LeaseUntil > latest.ObservedAt)) break;
                    await Task.Delay(250, deadline.Token);
                }
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            {
                var stopping = restarted.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;
                throw new TimeoutException($"Restarted Worker did not publish diagnostics; observed slots: {latest?.Workers.Count.ToString() ?? "unknown"}, host stopping: {stopping}.", error);
            }
        }
        catch (Exception exception) { restartFailure = exception; throw; }
        finally { await StopForCleanupAsync(restarted, restartFailure); }
    }, testContext.CancellationToken);

    private static async Task StopForCleanupAsync(IHost host, Exception? original)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await host.StopAsync(deadline.Token); }
        catch (Exception cleanup) when (original is not null) { throw new AggregateException("Worker test and shutdown both failed.", original, cleanup); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Database")]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PersistedJob_ExecutesAfterWorkerRestart() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        // Hangfire's process-global logger can outlive a previously disposed test host.
        GlobalConfiguration.Configuration.UseNoOpLogProvider();
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
        var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        var network = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
        await using (var db = new ResourcesDbContext(resources))
        {
            await db.Database.MigrateAsync(token);
            db.Devices.Add(new Device("Worker loopback", "127.0.0.1", false, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
        }
        await using (var db = new NetworkDbContext(network)) await db.Database.MigrateAsync(token);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBuilder = CreateBuilder(database.ConnectionString, completed);
        using var first = firstBuilder.Build();
        await first.StartAsync(token);
        using (var scope = first.Services.CreateScope())
        {
            var schedule = scope.ServiceProvider.GetRequiredService<OutboxScheduleManager>();
            var snapshot = schedule.GetSnapshot();
            Assert.AreEqual(1, snapshot.Minutes);
            schedule.SetIntervalMinutes(10, snapshot.Version, Guid.CreateVersion7());
            Assert.ThrowsExactly<ScheduleConflictException>(() => schedule.SetIntervalMinutes(5, snapshot.Version, Guid.CreateVersion7()));
            scope.ServiceProvider.GetRequiredService<IRecurringJobManager>().RemoveIfExists(OutboxScheduleManager.ScheduleId);
            Assert.IsFalse(schedule.GetSnapshot().Applied);
            schedule.EnsureDefault();
            Assert.IsTrue(schedule.GetSnapshot().Applied);
            Assert.AreEqual(10, schedule.GetSnapshot().Minutes);
        }
        await first.StopAsync(token);
        first.Services.GetRequiredService<IBackgroundJobClient>().Enqueue<ProbeJob>(job => job.RunAsync(CancellationToken.None));
        Assert.IsFalse(completed.Task.IsCompleted);
        var secondBuilder = CreateBuilder(database.ConnectionString, completed);
        using (var second = secondBuilder.Build())
        {
            await second.StartAsync(token);
            using (var scope = second.Services.CreateScope())
                Assert.AreEqual(10, scope.ServiceProvider.GetRequiredService<OutboxScheduleManager>().GetSnapshot().Minutes);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            while (true)
            {
                await using var db = new NetworkDbContext(network);
                if (await db.PingSamples.AnyAsync(x => x.Succeeded && x.Target == "127.0.0.1", deadline.Token)) break;
                await Task.Delay(100, deadline.Token);
            }
            using (var scope = second.Services.CreateScope())
            {
                var health = scope.ServiceProvider.GetRequiredService<SchedulingHealth>();
                var heartbeats = await health.ReadAsync(deadline.Token);
                Assert.IsTrue(heartbeats.Values.Any(heartbeat => heartbeat is { } value && value <= heartbeats.ObservedAt &&
                    heartbeats.ObservedAt - value < TimeSpan.FromMinutes(1)));
            }
            await second.StopAsync(token);
        }
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-outbox-dispatch'";
        Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
        command.CommandText = "SELECT Id, Version FROM scheduling.ScheduleSettings WHERE Name='vigiloom-outbox-dispatch'";
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.IsTrue(await reader.ReadAsync(token));
        Assert.AreEqual(7, reader.GetGuid(0).Version);
        Assert.HasCount(8, (byte[])reader[1]);
        await reader.DisposeAsync();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID('scheduling.ScheduleSettings')
              AND ((is_primary_key = 1 AND type_desc = 'NONCLUSTERED')
                OR (is_unique = 1 AND type_desc = 'CLUSTERED'))
            """;
        Assert.AreEqual(2, (int)(await command.ExecuteScalarAsync(token))!);
    }, testContext.CancellationToken);

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Database")]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task AutomaticSpeedJob_QueuesAndWorkerPersistsOneResult() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        GlobalConfiguration.Configuration.UseNoOpLogProvider();
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        var network = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
        await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(token);
        await using (var db = new NetworkDbContext(network)) await db.Database.MigrateAsync(token);
        var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
        var scheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new SchedulingDbContext(scheduling)) await db.Database.MigrateAsync(token);

        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString });
        var probe = new FixtureSpeedProbe();
        builder.Services.AddSingleton<ISpeedProbe>(probe);
        using var host = builder.Build();
        await host.StartAsync(token);
        try
        {
            using var scope = host.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SpeedSettings>();
            var initial = await settings.GetAsync(token);
            Assert.IsNotNull(await settings.UpdateAsync(new(true, 6, 100, 40, initial.Version), Guid.CreateVersion7(), token));
            await using (var db = new NetworkDbContext(network))
                await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedSettings SET NextAutomaticAt = DATEADD(minute,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE Name=N'central'", token);
            var jobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            var jobIds = Enumerable.Range(0, 3).Select(_ => jobs.Enqueue<AutomaticSpeedJob>(job => job.RunAsync(CancellationToken.None))).ToArray();
            using var jobConnection = scope.ServiceProvider.GetRequiredService<JobStorage>().GetConnection();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                await using var db = new NetworkDbContext(network);
                var states = jobIds.Select(id => jobConnection.GetStateData(id)?.Name).ToArray();
                Assert.DoesNotContain("Failed", states);
                if (states.All(state => state == "Succeeded") && await db.SpeedRuns.AnyAsync(x => x.State == SpeedTestState.Success, deadline.Token)) break;
                await Task.Delay(100, deadline.Token);
            }
            Assert.AreEqual(1, probe.Calls);
            await using var check = new NetworkDbContext(network);
            var result = await check.SpeedRuns.SingleAsync(token);
            Assert.IsTrue(result.Automatic);
            Assert.AreEqual("central", result.Location);
            Assert.AreEqual("fixture", result.Server);
            Assert.AreEqual(10d, result.DownloadMbps);
            Assert.AreEqual(5d, result.UploadMbps);
            Assert.IsNotNull(result.CompletedAt);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-speed-test'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM hangfire.[Set] WHERE [Key]='recurring-jobs' AND [Value]='vigiloom-speed-retention'";
            Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);

            var expired = new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow.AddDays(-366),
                State = SpeedTestState.Success, CompletedAt = DateTimeOffset.UtcNow.AddDays(-366).AddMinutes(1) };
            check.SpeedRuns.Add(expired);
            await check.SaveChangesAsync(token);
            var cleanupJob = jobs.Enqueue<SpeedRetentionJob>(job => job.RunAsync(CancellationToken.None));
            while (true)
            {
                var state = jobConnection.GetStateData(cleanupJob)?.Name;
                Assert.AreNotEqual("Failed", state);
                if (state == "Succeeded") break;
                await Task.Delay(100, deadline.Token);
            }
            await using var afterCleanup = new NetworkDbContext(network);
            Assert.IsFalse(await afterCleanup.SpeedRuns.AnyAsync(x => x.Id == expired.Id, token));
            Assert.IsTrue(await afterCleanup.SpeedRuns.AnyAsync(x => x.Id == result.Id, token));
            Assert.AreEqual(1L, (await afterCleanup.SpeedSettings.SingleAsync(token)).RetentionDeletedTotal);
        }
        finally { await host.StopAsync(CancellationToken.None); }
    }, testContext.CancellationToken);

    private sealed class FixtureSpeedProbe : ISpeedProbe
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task<SpeedMeasurement> MeasureAsync(SpeedLease lease, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new SpeedMeasurement(SpeedTestState.Success, "fixture", DownloadMbps: 10, UploadMbps: 5, LatencyMilliseconds: 3));
        }
    }

    private static HostApplicationBuilder CreateBuilder(string connectionString, TaskCompletionSource completed)
    {
        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = connectionString });
        builder.Services.AddSingleton(new ProbeJob(completed));
        return builder;
    }

    public sealed class ProbeJob(TaskCompletionSource completed)
    {
        public Task RunAsync(CancellationToken cancellationToken)
        {
            completed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
