using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Worker.Hosting;

namespace Vigiloom.Worker.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class FailedJobRetentionTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Cleanup_DeletesExpiredFailuresAndKeepsRecentOrRetriedJobs() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
            var builder = WorkerHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString });
            using var host = builder.Build();
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            using var scope = host.Services.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            var job = Job.FromExpression(() => Probe());
            var old = client.Create(job, new FailedState(new InvalidOperationException("old failure")));
            var recent = client.Create(job, new FailedState(new InvalidOperationException("recent failure")));
            var retried = client.Create(job, new FailedState(new InvalidOperationException("retried failure")));
            var changed = client.Create(job, new FailedState(new InvalidOperationException("new failure")));
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE [hangfire].[State] SET [CreatedAt] = '2020-01-01T00:00:00', [Data] = JSON_MODIFY([Data], '$.FailedAt', '2020-01-01T00:00:00.0000000Z') WHERE [JobId] IN (@old, @retried) AND [Name] = 'Failed'";
                command.Parameters.AddWithValue("@old", old);
                command.Parameters.AddWithValue("@retried", retried);
                Assert.AreEqual(2, await command.ExecuteNonQueryAsync(token));
                command.Parameters.Clear();
                command.CommandText = "UPDATE [hangfire].[State] SET [CreatedAt] = '2020-01-01T00:00:00' WHERE [JobId] = @changed AND [Name] = 'Failed'";
                command.Parameters.AddWithValue("@changed", changed);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
            }
            Assert.IsTrue(client.ChangeState(retried, new EnqueuedState(), FailedState.StateName));
            Assert.AreEqual(1, await scope.ServiceProvider.GetRequiredService<FailedJobRetention>().RunAsync(token));
            using var jobConnection = scope.ServiceProvider.GetRequiredService<JobStorage>().GetConnection();
            Assert.AreEqual("Deleted", jobConnection.GetStateData(old)?.Name);
            Assert.AreEqual("Failed", jobConnection.GetStateData(recent)?.Name);
            Assert.AreEqual("Enqueued", jobConnection.GetStateData(retried)?.Name);
            Assert.AreEqual("Failed", jobConnection.GetStateData(changed)?.Name);
        }, context.CancellationToken);

    [AutomaticRetry(Attempts = 0)]
    public static void Probe() { }
}
