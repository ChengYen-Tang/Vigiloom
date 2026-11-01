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

namespace Vigiloom.Worker.Tests;

[TestClass]
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
