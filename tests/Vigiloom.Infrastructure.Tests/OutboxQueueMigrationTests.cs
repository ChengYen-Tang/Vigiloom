using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Infrastructure.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class OutboxQueueMigrationTests(TestContext context)
{
    [TestMethod]
    public Task MovesOldDispatcherJobsWithoutMovingOtherJobs() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            GlobalConfiguration.Configuration.UseNoOpLogProvider();
            await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            services.AddVigiloomScheduling(configuration);
            using var provider = services.BuildServiceProvider();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var jobs = provider.GetRequiredService<IBackgroundJobClient>();
            provider.GetRequiredService<IRecurringJobManager>().AddOrUpdate(OutboxScheduleManager.ScheduleId,
                Job.FromExpression<OutboxDispatcher>(dispatcher => dispatcher.DispatchAsync(CancellationToken.None), "default"),
                Cron.Minutely(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            for (var i = 0; i < 120; i++)
                jobs.Create(Job.FromExpression<OutboxDispatcher>(dispatcher => dispatcher.DispatchAsync(CancellationToken.None)), new EnqueuedState());
            jobs.Create(Job.FromExpression(() => OtherJob()), new EnqueuedState());
            await using (var sql = new SqlConnection(database.ConnectionString))
            {
                await sql.OpenAsync(token);
                await using var command = sql.CreateCommand();
                command.CommandText = "UPDATE [hangfire].[JobQueue] SET [Queue] = 'default' WHERE [Queue] = 'web'";
                Assert.AreEqual(120, await command.ExecuteNonQueryAsync(token));
            }
            using var schedule = new OutboxSchedule(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<OutboxSchedule>>());
            await schedule.StartAsync(token);
            await schedule.StopAsync(token);
            var monitor = provider.GetRequiredService<JobStorage>().GetMonitoringApi();
            Assert.AreEqual(120L, monitor.EnqueuedCount(OutboxScheduleManager.Queue));
            Assert.AreEqual(1L, monitor.EnqueuedCount("default"));
            using var scope = provider.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<OutboxScheduleManager>();
            var snapshot = manager.GetSnapshot();
            Assert.IsTrue(snapshot.Applied);
            manager.SetIntervalMinutes(10, snapshot.Version, Guid.CreateVersion7());
            Assert.ThrowsExactly<ScheduleConflictException>(() => manager.SetIntervalMinutes(5, snapshot.Version, Guid.CreateVersion7()));
            provider.GetRequiredService<IRecurringJobManager>().RemoveIfExists(OutboxScheduleManager.ScheduleId);
            Assert.IsFalse(manager.GetSnapshot().Applied);
            manager.EnsureDefault();
            Assert.AreEqual(10, manager.GetSnapshot().Minutes);
            Assert.IsTrue(manager.GetSnapshot().Applied);
        }, context.CancellationToken);

    public static void OtherJob() { }
}
