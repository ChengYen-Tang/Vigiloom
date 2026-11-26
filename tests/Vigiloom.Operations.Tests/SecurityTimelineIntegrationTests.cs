extern alias SecurityHost;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Contracts.Events;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SecurityTimelineIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task CancelledYaraAppearsInOperationsTimelineThroughTemporal()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL_ISOLATED");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL_ISOLATED to a disposable Temporal instance.");
        return SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, outerToken) =>
            await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (securityDb, token) =>
            {
                var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(operationsDb.ConnectionString,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
                await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
                var securityOptions = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(securityDb.ConnectionString,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
                await using (var db = new SecurityAnalysisDbContext(securityOptions)) await db.Database.MigrateAsync(token);
                foreach (var connection in new[] { operationsDb.ConnectionString, securityDb.ConnectionString })
                {
                    var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(connection,
                        sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
                    await using var db = new SchedulingDbContext(options);
                    await db.Database.MigrateAsync(token);
                }
                var queue = "vigiloom-timeline-test-" + Guid.CreateVersion7().ToString("N");
                using var operations = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
                {
                    web.UseSetting("Hangfire:Enabled", "false");
                    web.UseSetting("Work:OperationsQueue", queue);
                    web.UseSetting("Work:AuditQueue", queue + "-audit");
                    web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString, ["Temporal:Address"] = address }));
                });
                using var operationsClient = operations.CreateClient();
                using var security = new WebApplicationFactory<SecurityHost::Program>().WithWebHostBuilder(web =>
                {
                    web.UseSetting("Work:SecurityQueue", queue + "-security");
                    web.UseSetting("Work:SecurityLogQueue", queue + "-logs");
                    web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ConnectionStrings:Security"] = securityDb.ConnectionString, ["Temporal:Address"] = address,
                        ["Work:OperationsQueue"] = queue, ["Work:AgentQueue"] = queue + "-agent" }));
                });
                using var securityClient = security.CreateClient();
                var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
                using var created = await securityClient.PostAsJsonAsync("/internal/v1/yara-scans/create", new
                {
                    Identity = identity, Request = new YaraScanRequest("/tmp/test", "rule test { condition: true }", 1024, 30), ActorId = Guid.CreateVersion7()
                }, token);
                created.EnsureSuccessStatusCode();
                var job = await created.Content.ReadFromJsonAsync<YaraScanJob>(cancellationToken: token);
                using var cancelled = await securityClient.PostAsync($"/internal/v1/yara-scans/devices/{identity.DeviceId:D}/{job!.Id:D}/cancel", null, token);
                Assert.AreEqual(HttpStatusCode.NoContent, cancelled.StatusCode);
                var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
                var until = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
                var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
                EventTimelinePage? page;
                do
                {
                    page = await operationsClient.GetFromJsonAsync<EventTimelinePage>($"/internal/v1/events?source=yara&from={from}&until={until}", token);
                    if (page!.Items.Count != 0) break;
                    await Task.Delay(250, token);
                } while (DateTimeOffset.UtcNow < deadline);
                var entry = Assert.ContainsSingle(page!.Items);
                Assert.AreEqual(identity.DeviceId, entry.ResourceId);
                Assert.Contains(job.Id.ToString("D"), entry.Evidence);
                Assert.Contains("/tmp/test", entry.Summary);
            }, outerToken), context.CancellationToken);
    }
}
