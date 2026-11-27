extern alias HostMonitoring;

using Hangfire;
using Hangfire.Common;
using Hangfire.SqlServer;
using Hangfire.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class ScheduleRetirementTests(TestContext context)
{
    [TestMethod]
    public Task HostAndOperationsRetireTheirOldRecurringJobs() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            GlobalConfiguration.Configuration.UseNoOpLogProvider();
            await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
            var storage = new SqlServerStorage(database.ConnectionString, SchedulingRegistration.StorageOptions(prepareSchema: false));
            var recurring = new RecurringJobManager(storage);
            string[] oldHost = ["vigiloom-host-metric-rollup", "vigiloom-host-metric-retention", "vigiloom-agent-inbox-retention"];
            string[] oldOperations = ["vigiloom-line-delivery", "vigiloom-agent-package-refresh", "vigiloom-settings-audit-retention"];
            foreach (var id in oldHost.Concat(oldOperations))
                recurring.AddOrUpdate(id, Job.FromExpression(() => Probe()), Cron.Hourly(), new RecurringJobOptions());

            using (var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web => Configure(web, database.ConnectionString, "HostMonitoring")))
            using (var client = host.CreateClient())
            using (var connection = host.Services.GetRequiredService<JobStorage>().GetConnection())
            {
                var jobs = connection.GetRecurringJobs();
                Assert.IsFalse(jobs.Any(job => oldHost.Contains(job.Id)));
                Assert.IsTrue(jobs.Any(job => job.Id == "vigiloom-host-service-metric-rollup" && job.Job.Queue == "host"));
            }
            GlobalConfiguration.Configuration.UseNoOpLogProvider();
            using (var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web => Configure(web, database.ConnectionString, "Operations")))
            using (var client = host.CreateClient())
            using (var connection = host.Services.GetRequiredService<JobStorage>().GetConnection())
            {
                var jobs = connection.GetRecurringJobs();
                Assert.IsFalse(jobs.Any(job => oldOperations.Contains(job.Id)));
                Assert.IsTrue(jobs.Any(job => job.Id == "vigiloom-operations-retention" && job.Job.Queue == "operations"));
            }
        }, context.CancellationToken);

    private static void Configure(IWebHostBuilder web, string connectionString, string service)
    {
        web.UseSetting("Hangfire:Enabled", "true");
        web.UseSetting("Temporal:Enabled", "false");
        web.UseSetting("Services:Web:Url", "http://web");
        web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Scheduling"] = connectionString, [$"ConnectionStrings:{service}"] = connectionString }));
        web.ConfigureTestServices(services =>
        {
            HostWorkers.RemoveTemporal(services);
            foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService) &&
                (item.ImplementationType?.Name is "WorkDispatcher" or "LineAggregationWorker" ||
                 item.ImplementationFactory?.Method.Name.Contains("AddHangfireServerInner", StringComparison.Ordinal) == true)).ToArray())
                services.Remove(descriptor);
        });
    }

    public static void Probe() { }
}
