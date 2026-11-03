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
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;

namespace Vigiloom.Worker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HostLifecycleTests(TestContext testContext)
{
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
