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
            var bootId = Guid.CreateVersion7();
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
            using var sent = await client.PostAsJsonAsync("/internal/v1/diagnostics/workers", new NetworkDiagnosticUpdate(bootId,
                new(DateTimeOffset.UtcNow, null, 0, ScannerDiagnosticFailure.None), new(null, DateTimeOffset.UtcNow, 2, ScannerDiagnosticFailure.Timeout)), token);
            sent.EnsureSuccessStatusCode();
            var secondBoot = Guid.CreateVersion7();
            using var second = await client.PostAsJsonAsync("/internal/v1/diagnostics/workers", new NetworkDiagnosticUpdate(secondBoot,
                new(DateTimeOffset.UtcNow, null, 0, ScannerDiagnosticFailure.None), new(null, null, 0, ScannerDiagnosticFailure.None)), token);
            second.EnsureSuccessStatusCode();
            using var updated = await client.PostAsJsonAsync("/internal/v1/diagnostics/workers", new NetworkDiagnosticUpdate(bootId,
                new(DateTimeOffset.UtcNow, null, 0, ScannerDiagnosticFailure.None), new(null, DateTimeOffset.UtcNow, 2, ScannerDiagnosticFailure.Timeout)), token);
            updated.EnsureSuccessStatusCode();
            var overview = await client.GetFromJsonAsync<WorkOverview>("/internal/v1/diagnostics/jobs", token);
            Assert.IsNotNull(overview);
            Assert.AreEqual(1, overview.PendingMessages);
            Assert.AreEqual(0, overview.FailedMessages);
            var heartbeat = await client.GetFromJsonAsync<SchedulerHeartbeatsInfo>("/internal/v1/diagnostics/scheduler", token);
            Assert.HasCount(1, heartbeat!.Values);
            Assert.IsNotNull(heartbeat.Values[0]);
            var workers = await client.GetFromJsonAsync<WorkerDiagnosticsInfo>("/internal/v1/diagnostics/workers", token);
            Assert.HasCount(2, workers!.Workers);
            var actual = workers.Workers.Single(item => item.BootId == bootId);
            Assert.AreEqual(2, actual.Speed.ConsecutiveFailures);
            Assert.AreEqual(ScannerDiagnosticFailure.Timeout, actual.Speed.FailureKind);
            await using (var db = await operationsFactory.CreateDbContextAsync(token))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [operations].[NetworkDiagnostics] SET [LeaseUntil] = DATEADD(second, -1, SYSDATETIMEOFFSET()) WHERE [Id] = {secondBoot}", token);
            Assert.AreEqual(1, await new NetworkDiagnosticsStore(operationsFactory).CleanupExpiredAsync(token));
            workers = await client.GetFromJsonAsync<WorkerDiagnosticsInfo>("/internal/v1/diagnostics/workers", token);
            Assert.AreEqual(bootId, Assert.ContainsSingle(workers!.Workers).BootId);
        }, outerToken), context.CancellationToken);
}
