using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class FailedJobRetentionTests(TestContext context)
{
    [TestMethod]
    public Task OperationsRetentionCleansExpiredHangfireFailures() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (scheduling, token) =>
        {
            GlobalConfiguration.Configuration.UseNoOpLogProvider();
            await HangfireSchemaInstaller.InstallAsync(scheduling.ConnectionString, token);
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "true");
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Operations"] = database.ConnectionString,
                    ["ConnectionStrings:Scheduling"] = scheduling.ConnectionString
                }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.RemoveAll<IAuditRetentionSource>();
                    services.RemoveAll<IBackgroundHistorySource>();
                });
            });
            using var client = host.CreateClient();
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using var scope = host.Services.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            var old = jobs.Create(Job.FromExpression(() => Probe()), new FailedState(new InvalidOperationException("old")));
            var recent = jobs.Create(Job.FromExpression(() => Probe()), new FailedState(new InvalidOperationException("recent")));
            await using (var connection = new SqlConnection(scheduling.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE [hangfire].[State] SET [CreatedAt] = '2020-01-01T00:00:00', [Data] = JSON_MODIFY([Data], '$.FailedAt', '2020-01-01T00:00:00.0000000Z') WHERE [JobId] = @old AND [Name] = 'Failed'";
                command.Parameters.AddWithValue("@old", old);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
            }
            await scope.ServiceProvider.GetRequiredService<OperationsRetentionJob>().RunAsync(token);
            using var jobConnection = scope.ServiceProvider.GetRequiredService<JobStorage>().GetConnection();
            Assert.AreEqual("Deleted", jobConnection.GetStateData(old)?.Name);
            Assert.AreEqual("Failed", jobConnection.GetStateData(recent)?.Name);
        }, outerToken), context.CancellationToken);

    [AutomaticRetry(Attempts = 0)]
    public static void Probe() { }
}
