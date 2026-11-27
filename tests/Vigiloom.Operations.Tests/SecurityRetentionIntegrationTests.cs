extern alias SecurityHost;

using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SecurityRetentionIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task OperationsPolicyControlsSecurityRetention() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (securityDb, token) =>
        {
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
            var securityOptions = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(securityDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var securityScheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(securityDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(securityScheduling))
            {
                await db.Database.MigrateAsync(token);
                var old = DateTimeOffset.UtcNow.AddDays(-40);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO scheduling.Outbox (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts, CompletedAt) VALUES ({Guid.CreateVersion7()}, {TemporalOutboxHandler.Type}, {"{}"}, {old}, {old}, {2}, {1}, {old})", token);
            }
            await using (var db = new SecurityAnalysisDbContext(securityOptions))
            {
                await db.Database.MigrateAsync(token);
                var now = DateTimeOffset.UtcNow;
                foreach (var age in new[] { 40, 5 })
                    db.YaraScans.Add(new YaraScan { Id = Guid.CreateVersion7(), AgentId = Guid.CreateVersion7(), DeviceId = Guid.CreateVersion7(),
                        RequestedBy = Guid.CreateVersion7(), FilePath = $"/tmp/{age}", Rules = "rule test { condition: true }", MaximumBytes = 1024,
                        TimeoutSeconds = 30, RequestedAt = now.AddDays(-age), CompletedAt = now.AddDays(-age) });
                await db.SaveChangesAsync(token);
            }
            using var security = new WebApplicationFactory<SecurityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = securityDb.ConnectionString }));
            });
            using var securityClient = security.CreateClient();
            using var operations = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Testing");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Services:Security:Url", "http://security");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.AddHttpClient<RemoteSecurityRetention>().ConfigurePrimaryHttpMessageHandler(() => security.Server.CreateHandler());
                    services.AddHttpClient("work-history-Security").ConfigurePrimaryHttpMessageHandler(() => security.Server.CreateHandler());
                });
            });
            using var client = operations.CreateClient();
            var settings = await client.GetFromJsonAsync<AuditRetentionSnapshot>("/internal/v1/history/retention", token);
            var actor = Guid.CreateVersion7();
            using var previewResponse = await client.PostAsJsonAsync("/internal/v1/history/retention/preview", new RetentionPreviewChange(actor, new(30, settings!.Version)), token);
            previewResponse.EnsureSuccessStatusCode();
            var preview = await previewResponse.Content.ReadFromJsonAsync<AuditRetentionPreview>(cancellationToken: token);
            Assert.AreEqual(1L, preview!.AffectedRecords);
            Assert.AreEqual(1L, preview.BackgroundJobs);
            using var updated = await client.PutAsJsonAsync("/internal/v1/history/retention", new RetentionChange(actor, new(30, settings.Version, preview!.ConfirmationToken)), token);
            updated.EnsureSuccessStatusCode();
            await using (var scope = operations.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AuditRetentionCleanup>().RunAsync(token);
            await using var check = new SecurityAnalysisDbContext(securityOptions);
            Assert.AreEqual("/tmp/5", (await check.YaraScans.SingleAsync(token)).FilePath);
            await using var checkJobs = new SchedulingDbContext(securityScheduling);
            Assert.AreEqual(0, await checkJobs.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM scheduling.Outbox").SingleAsync(token));
        }, outerToken), context.CancellationToken);
}
