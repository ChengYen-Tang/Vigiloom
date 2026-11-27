using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Scheduling;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class LegacyWorkerRetirementTests(TestContext context)
{
    [TestMethod]
    public Task RetiresOnlySupersededTriggersWhenTheirOwnerIsReady() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            GlobalConfiguration.Configuration.UseNoOpLogProvider();
            await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddLogging();
            services.AddVigiloomScheduling(config);
            using var provider = services.BuildServiceProvider();
            var jobs = provider.GetRequiredService<IBackgroundJobClient>();
            var scheduled = jobs.Create(Job.FromExpression(() => Probe(CancellationToken.None)), new ScheduledState(TimeSpan.FromHours(1)));
            var enqueued = jobs.Create(Job.FromExpression(() => Probe(CancellationToken.None)), new EnqueuedState());
            var unrelated = jobs.Create(Job.FromExpression(() => Probe(CancellationToken.None)), new EnqueuedState());
            var payload = new InvocationData("Vigiloom.Worker.Hosting.PingRetentionJob, Vigiloom.Worker", "RunAsync",
                JsonSerializer.Serialize(new[] { typeof(CancellationToken).AssemblyQualifiedName }), "[null]").SerializePayload();
            await using (var sql = new SqlConnection(database.ConnectionString))
            {
                await sql.OpenAsync(token);
                await using var command = sql.CreateCommand();
                command.CommandText = "UPDATE [hangfire].[Job] SET [InvocationData] = @payload WHERE [Id] IN (@scheduled, @enqueued)";
                command.Parameters.AddWithValue("@payload", payload);
                command.Parameters.AddWithValue("@scheduled", long.Parse(scheduled));
                command.Parameters.AddWithValue("@enqueued", long.Parse(enqueued));
                Assert.AreEqual(2, await command.ExecuteNonQueryAsync(token));
            }
            var storage = provider.GetRequiredService<JobStorage>();
            var retirement = new LegacyWorkerRetirement(config, storage, jobs);
            Assert.IsFalse(await retirement.RunAsync(token));
            provider.GetRequiredService<IRecurringJobManager>().AddOrUpdate("vigiloom-ping-retention",
                Job.FromExpression<NetworkPingRetentionJob>(job => job.RunAsync(CancellationToken.None), "network"),
                Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            Assert.IsFalse(await retirement.RunAsync(token));
            config["Services:Network:Url"] = "http://network";
            provider.GetRequiredService<IRecurringJobManager>().AddOrUpdate("test-legacy-worker",
                Job.FromExpression(() => Probe(CancellationToken.None)), Cron.Hourly(), new RecurringJobOptions());
            await using (var sql = new SqlConnection(database.ConnectionString))
            {
                await sql.OpenAsync(token);
                await using var command = sql.CreateCommand();
                command.CommandText = "UPDATE [hangfire].[Hash] SET [Value] = @payload WHERE [Key] = 'recurring-job:test-legacy-worker' AND [Field] = 'Job'";
                command.Parameters.AddWithValue("@payload", payload);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
            }
            Assert.IsFalse(await retirement.RunAsync(token));
            provider.GetRequiredService<IRecurringJobManager>().RemoveIfExists("test-legacy-worker");
            Assert.IsTrue(await retirement.RunAsync(token));
            using var connection = storage.GetConnection();
            Assert.AreEqual("Deleted", connection.GetStateData(scheduled)?.Name);
            Assert.AreEqual("Deleted", connection.GetStateData(enqueued)?.Name);
            Assert.AreEqual("Enqueued", connection.GetStateData(unrelated)?.Name);
            Assert.AreEqual(1L, storage.GetMonitoringApi().EnqueuedCount("default"));
        }, context.CancellationToken);

    public static Task Probe(CancellationToken token) => Task.CompletedTask;
}
