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
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Microsoft.Data.SqlClient;
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
            services.AddModuleSqlServer<OperationsDbContext>(operations.ConnectionString, "operations");
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
            var operationsFactory = provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            await using (var db = await operationsFactory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var workerStore = new WorkerDiagnosticsStore(operationsFactory);
            var bootId = Guid.CreateVersion7();
            var slot = await workerStore.ClaimAsync(bootId, token);
            Assert.IsNotNull(slot);
            Assert.IsTrue(await workerStore.UpdateAsync(slot.Value, bootId,
                new WorkerObservation(new ScannerObservation(DateTimeOffset.UtcNow, null, 0), new ScannerObservation(null, DateTimeOffset.UtcNow, 2, ScannerFailureKind.Timeout)), token));
            await HangfireSchemaInstaller.InstallAsync(scheduling.ConnectionString, token);
            await using (var connection = new SqlConnection(scheduling.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var command = new SqlCommand("INSERT INTO hangfire.Server (Id, Data, LastHeartbeat) VALUES ('test-worker', '{}', SYSUTCDATETIME())", connection);
                await command.ExecuteNonQueryAsync(token);
            }
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
            var heartbeat = await client.GetFromJsonAsync<SchedulerHeartbeatsInfo>("/internal/v1/diagnostics/scheduler", token);
            Assert.HasCount(1, heartbeat!.Values);
            Assert.IsNotNull(heartbeat.Values[0]);
            var workers = await client.GetFromJsonAsync<WorkerDiagnosticsInfo>("/internal/v1/diagnostics/workers", token);
            Assert.HasCount(1, workers!.Workers);
            Assert.AreEqual(bootId, workers.Workers[0].BootId);
            Assert.AreEqual(2, workers.Workers[0].Speed.ConsecutiveFailures);
            Assert.AreEqual(ScannerDiagnosticFailure.Timeout, workers.Workers[0].Speed.FailureKind);
        }, outerToken), context.CancellationToken);
}
