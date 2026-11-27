extern alias HostMonitoring;

using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Operations;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostRetentionIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task OperationsPolicyControlsHostAuditRetention() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (hostDb, token) =>
        {
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
            var hostOptions = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(hostDb.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            await using (var db = new HostMonitoringDbContext(hostOptions))
            {
                await db.Database.MigrateAsync(token);
                var now = DateTimeOffset.UtcNow;
                var setting = new AgentSetting { UpdatedBy = Guid.CreateVersion7(), UpdatedAt = now };
                db.Add(setting);
                await db.SaveChangesAsync(token);
                foreach (var days in new[] { 40, 5 })
                    db.Add(new AgentSettingAudit { SettingId = setting.Id, ActorId = Guid.CreateVersion7(), OccurredAt = now.AddDays(-days),
                        EffectiveHeartbeatSeconds = 30, EffectiveSettingsPollSeconds = 300, EffectiveBatchIntervalSeconds = 60,
                        EffectiveRevision = "test", ChangeVersion = new byte[8] });
                await db.SaveChangesAsync(token);
            }
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:HostMonitoring"] = hostDb.ConnectionString }));
                web.ConfigureTestServices(HostWorkers.RemoveBackgroundWork);
            });
            using var hostClient = host.CreateClient();
            using var operations = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Testing");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Services:HostMonitoring:Url", "http://host");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.AddHttpClient<RemoteHostAuditRetention>().ConfigurePrimaryHttpMessageHandler(() => host.Server.CreateHandler());
                });
            });
            using var client = operations.CreateClient();
            var settings = await client.GetFromJsonAsync<AuditRetentionSnapshot>("/internal/v1/history/retention", token);
            var actor = Guid.CreateVersion7();
            using var previewResponse = await client.PostAsJsonAsync("/internal/v1/history/retention/preview", new RetentionPreviewChange(actor, new(30, settings!.Version)), token);
            previewResponse.EnsureSuccessStatusCode();
            var preview = await previewResponse.Content.ReadFromJsonAsync<AuditRetentionPreview>(cancellationToken: token);
            Assert.AreEqual(1L, preview!.AffectedRecords);
            using var updated = await client.PutAsJsonAsync("/internal/v1/history/retention", new RetentionChange(actor, new(30, settings.Version, preview.ConfirmationToken)), token);
            updated.EnsureSuccessStatusCode();
            await using (var scope = operations.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AuditRetentionCleanup>().RunAsync(token);
            await using var check = new HostMonitoringDbContext(hostOptions);
            Assert.IsTrue((await check.Set<AgentSettingAudit>().SingleAsync(token)).OccurredAt > DateTimeOffset.UtcNow.AddDays(-30));
        }, outerToken), context.CancellationToken);
}
