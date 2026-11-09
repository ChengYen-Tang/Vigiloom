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
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;

namespace Vigiloom.Worker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HostLifecycleTests(TestContext testContext)
{
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
    [Timeout(120000, CooperativeCancellation = true)]
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
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                var snapshot = await store.GetSnapshotAsync(deadline.Token);
                if (snapshot.Workers.Any(worker => worker.BootId != originalBoot && worker.LeaseUntil > snapshot.ObservedAt)) break;
                await Task.Delay(250, deadline.Token);
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
