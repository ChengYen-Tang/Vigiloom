extern alias IdentityHost;

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
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IdentityRetentionIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task OperationsPolicyControlsIdentityRetention() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (identityDb, token) =>
        {
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
            var identityOptions = new DbContextOptionsBuilder<IdentityAccessDbContext>().UseSqlServer(identityDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options;
            await using (var db = new IdentityAccessDbContext(identityOptions))
            {
                await db.Database.MigrateAsync(token);
                var account = Guid.CreateVersion7();
                var now = DateTimeOffset.UtcNow;
                db.AccountAuditEvents.AddRange(new AccountAuditEvent(null, account, "Updated", now.AddDays(-40)),
                    new AccountAuditEvent(null, account, "Updated", now.AddDays(-5)),
                    new AccountAuditEvent(null, account, "InitialAdministratorCreated", now.AddDays(-40)));
                await db.SaveChangesAsync(token);
            }
            using var identity = new WebApplicationFactory<IdentityHost::Program>().WithWebHostBuilder(web =>
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:VigiloomIdentity"] = identityDb.ConnectionString })));
            using var identityClient = identity.CreateClient();
            using var operations = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Testing");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Services:Identity:Url", "http://identity");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.AddHttpClient<RemoteIdentityAuditRetention>().ConfigurePrimaryHttpMessageHandler(() => identity.Server.CreateHandler());
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
            await using var check = new IdentityAccessDbContext(identityOptions);
            var remaining = await check.AccountAuditEvents.ToArrayAsync(token);
            Assert.HasCount(2, remaining);
            Assert.IsTrue(remaining.Any(item => item.Action == "InitialAdministratorCreated"));
            Assert.IsTrue(remaining.Any(item => item.Action == "Updated"));
        }, outerToken), context.CancellationToken);
}
