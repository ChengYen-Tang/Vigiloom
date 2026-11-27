extern alias ResourcesHost;

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
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ResourcesRetentionIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task OperationsPolicyControlsDeviceAuditRetention() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (resourcesDb, token) =>
        {
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
            var resourcesOptions = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(resourcesDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            var resourcesScheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(resourcesDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(resourcesScheduling)) await db.Database.MigrateAsync(token);
            await using (var db = new ResourcesDbContext(resourcesOptions))
            {
                await db.Database.MigrateAsync(token);
                var now = DateTimeOffset.UtcNow;
                var device = new Device("retention-device", "127.0.0.1", false, now);
                db.Devices.Add(device);
                db.DeviceAudits.AddRange(new DeviceAudit(device.Id, Guid.CreateVersion7(), "updated", now.AddDays(-40)),
                    new DeviceAudit(device.Id, Guid.CreateVersion7(), "updated", now.AddDays(-5)));
                await db.SaveChangesAsync(token);
            }
            using var resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Resources"] = resourcesDb.ConnectionString }));
                web.ConfigureTestServices(HostWorkers.RemoveBackgroundWork);
            });
            using var resourcesClient = resources.CreateClient();
            using var operations = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Testing");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Services:Resources:Url", "http://resources");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.AddHttpClient<RemoteResourcesAuditRetention>().ConfigurePrimaryHttpMessageHandler(() => resources.Server.CreateHandler());
                    services.AddHttpClient("work-history-Resources").ConfigurePrimaryHttpMessageHandler(() => resources.Server.CreateHandler());
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
            await using var check = new ResourcesDbContext(resourcesOptions);
            Assert.IsTrue((await check.DeviceAudits.SingleAsync(token)).OccurredAt > DateTimeOffset.UtcNow.AddDays(-30));
        }, outerToken), context.CancellationToken);
}
