extern alias OperationsHost;

using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Operations;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LegacyDashboardRetentionTests(TestContext context)
{
    [TestMethod]
    public Task OperationsPolicyCleansLegacyDashboardHistory() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (platform, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, token) =>
        {
            using var web = PingTestSite.CreateFactory(platform.ConnectionString);
            await using (var db = await web.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                var auditActor = Guid.CreateVersion7();
                var old = DateTimeOffset.UtcNow.AddDays(-40);
                var recent = DateTimeOffset.UtcNow;
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [scheduling].[DashboardAudit] ([ActorId], [OccurredAt], [Action], [Result]) VALUES ({auditActor}, {old}, '/jobs/delete', 'accepted'), ({auditActor}, {old}, '/jobs/delete', 'started'), ({auditActor}, {recent}, '/jobs/delete', 'accepted')", token);
            }
            using var operations = new WebApplicationFactory<OperationsHost::Program>().WithWebHostBuilder(host =>
            {
                host.UseSetting("Hangfire:Enabled", "false");
                host.UseSetting("Temporal:Enabled", "false");
                host.UseSetting("Services:Web:Url", "http://web");
                host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString }));
                host.ConfigureTestServices(services =>
                {
                    foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType?.Namespace?.StartsWith("Vigiloom.", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(descriptor);
                    services.RemoveAll<IAuditRetentionSource>();
                    services.RemoveAll<IBackgroundHistorySource>();
                    services.AddHttpClient<RemoteDashboardAuditRetention>().ConfigurePrimaryHttpMessageHandler(() => web.Server.CreateHandler());
                    services.AddScoped<IAuditRetentionSource>(provider => provider.GetRequiredService<RemoteDashboardAuditRetention>());
                });
            });
            using var client = operations.CreateClient();
            await using (var db = await operations.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await operations.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var settings = await client.GetFromJsonAsync<AuditRetentionSnapshot>("/internal/v1/history/retention", token);
            var actor = Guid.CreateVersion7();
            using var previewResponse = await client.PostAsJsonAsync("/internal/v1/history/retention/preview", new RetentionPreviewChange(actor, new(30, settings!.Version)), token);
            previewResponse.EnsureSuccessStatusCode();
            var preview = await previewResponse.Content.ReadFromJsonAsync<AuditRetentionPreview>(cancellationToken: token);
            Assert.AreEqual(1L, preview!.AffectedRecords);
            using var updated = await client.PutAsJsonAsync("/internal/v1/history/retention", new RetentionChange(actor, new(30, settings.Version, preview.ConfirmationToken)), token);
            updated.EnsureSuccessStatusCode();
            await using (var scope = operations.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<AuditRetentionCleanup>().RunAsync(token);
            await using var check = await web.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [scheduling].[DashboardAudit] WHERE [Result] = 'started'").SingleAsync(token));
            Assert.AreEqual(1, await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [scheduling].[DashboardAudit] WHERE [Result] = 'accepted'").SingleAsync(token));
        }, outerToken), context.CancellationToken);
}
