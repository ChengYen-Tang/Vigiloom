using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DiagnosticsTests(TestContext context)
{
    [TestMethod]
    public Task SharedHangfireAndLocalOutboxUseTheirOwnDatabases() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operations, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (scheduling, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<SchedulingDbContext>(operations.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                var id = Guid.CreateVersion7();
                var payload = "{}";
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO scheduling.Outbox (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts)
                    VALUES ({id}, 'test', {payload}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 0, 0)
                    """, token);
            }
            await HangfireSchemaInstaller.InstallAsync(scheduling.ConnectionString, token);
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Operations"] = operations.ConnectionString,
                    ["ConnectionStrings:Scheduling"] = scheduling.ConnectionString
                }));
                web.ConfigureTestServices(HostWorkers.RemoveBackgroundWork);
            });
            using var client = host.CreateClient();
            var overview = await client.GetFromJsonAsync<WorkOverview>("/internal/v1/diagnostics/jobs", token);
            Assert.IsNotNull(overview);
            Assert.AreEqual(1, overview.PendingMessages);
            Assert.AreEqual(0, overview.FailedMessages);
        }, outerToken), context.CancellationToken);
}
