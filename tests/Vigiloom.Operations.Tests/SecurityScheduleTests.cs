extern alias SecurityHost;

using Hangfire;
using Hangfire.Common;
using Hangfire.SqlServer;
using Hangfire.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class SecurityScheduleTests(TestContext context)
{
    [TestMethod]
    public Task EnabledSecurityServiceRegistersItsOwnRecurringJobs() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            GlobalConfiguration.Configuration.UseNoOpLogProvider();
            await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
            var storage = new SqlServerStorage(database.ConnectionString, SchedulingRegistration.StorageOptions(prepareSchema: false));
            var oldJobs = new RecurringJobManager(storage);
            foreach (var id in new[] { "vigiloom-inventory-expiry", "vigiloom-intelligence-refresh", "vigiloom-debian-refresh" })
                oldJobs.AddOrUpdate(id, Job.FromExpression(() => Probe()), Cron.Hourly(), new RecurringJobOptions());
            using var host = new WebApplicationFactory<SecurityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "true");
                web.UseSetting("Temporal:Enabled", "true");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Scheduling"] = database.ConnectionString, ["ConnectionStrings:Security"] = database.ConnectionString }));
                web.ConfigureTestServices(HostWorkers.RemoveBackgroundWork);
            });
            using var client = host.CreateClient();
            using var connection = host.Services.GetRequiredService<JobStorage>().GetConnection();
            var jobs = connection.GetRecurringJobs();
            foreach (var id in new[] { "vigiloom-security-inventory-expiry", "vigiloom-security-integrity-expiry", "vigiloom-security-intelligence-refresh", "vigiloom-security-debian-refresh", "vigiloom-security-msrc-cvrf-refresh" })
                Assert.AreEqual("security", jobs.Single(job => job.Id == id).Job.Queue);
            Assert.IsFalse(jobs.Any(job => job.Id is "vigiloom-inventory-expiry" or "vigiloom-intelligence-refresh" or "vigiloom-debian-refresh"));
        }, context.CancellationToken);

    public static void Probe() { }
}
