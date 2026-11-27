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
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LegacyPlatformRetentionTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task OperationsPolicyCleansLegacyPlatformHistory(bool sameDatabase) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), (platform, outerToken) =>
            sameDatabase ? RunAsync(platform, platform, outerToken) :
            SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), (operationsDb, token) => RunAsync(platform, operationsDb, token), outerToken), context.CancellationToken);

    private static async Task RunAsync(SqlTestDatabase platform, SqlTestDatabase operationsDb, CancellationToken token)
    {
            using var web = PingTestSite.CreateFactory(platform.ConnectionString);
            await using (var db = await web.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await web.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                var now = DateTimeOffset.UtcNow;
                var setting = new AgentSetting { UpdatedBy = Guid.CreateVersion7(), UpdatedAt = now };
                db.Add(setting);
                await db.SaveChangesAsync(token);
                db.Add(new AgentSettingAudit { SettingId = setting.Id, ActorId = Guid.CreateVersion7(), OccurredAt = now.AddDays(-40),
                    EffectiveHeartbeatSeconds = 30, EffectiveSettingsPollSeconds = 300, EffectiveBatchIntervalSeconds = 60,
                    EffectiveRevision = "test", ChangeVersion = new byte[8] });
                await db.SaveChangesAsync(token);
            }
            await using (var db = await web.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await web.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                var actorId = Guid.CreateVersion7();
                var setting = new InventorySetting { UpdatedBy = actorId, UpdatedAt = DateTimeOffset.UtcNow };
                db.InventorySettings.Add(setting);
                db.InventorySettingAudits.Add(new InventorySettingAudit { SettingId = setting.Id, ActorId = actorId,
                    OccurredAt = DateTimeOffset.UtcNow.AddDays(-40), EffectiveScanIntervalSeconds = 3600,
                    EffectiveRevision = "test", ChangeVersion = new byte[8] });
                await db.SaveChangesAsync(token);
            }
            await using (var db = await web.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                var auditActor = Guid.CreateVersion7();
                var old = DateTimeOffset.UtcNow.AddDays(-40);
                var recent = DateTimeOffset.UtcNow;
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [scheduling].[DashboardAudit] ([ActorId], [OccurredAt], [Action], [Result]) VALUES ({auditActor}, {old}, '/jobs/delete', 'accepted'), ({auditActor}, {old}, '/jobs/delete', 'started'), ({auditActor}, {recent}, '/jobs/delete', 'accepted')", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [scheduling].[Outbox] ([MessageId], [MessageType], [Payload], [CreatedAt], [AvailableAt], [State], [Attempts], [CompletedAt]) VALUES ({Guid.CreateVersion7()}, {"test.done"}, {"{}"}, {old}, {old}, {2}, {1}, {old}), ({Guid.CreateVersion7()}, {"test.pending"}, {"{}"}, {old}, {old}, {0}, {0}, {null})", token);
            }
            await using (var db = await web.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                var old = DateTimeOffset.UtcNow.AddDays(-40);
                db.Set<PingAlert>().AddRange(new PingAlert { DeviceId = Guid.CreateVersion7(), Target = "open", OpenedAt = old },
                    new PingAlert { DeviceId = Guid.CreateVersion7(), Target = "closed", OpenedAt = old, RecoveredAt = old });
                db.Set<Notification>().Add(new Notification { MessageId = Guid.CreateVersion7(), Source = "ping", Title = "Old", Detail = "", OccurredAt = old });
                await db.SaveChangesAsync(token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [operations].[AuditEvents] ([ActorId], [ResourceId], [OccurredAt], [Kind], [Details]) VALUES ({Guid.CreateVersion7()}, {Guid.CreateVersion7()}, {old}, {1}, {"{}"})", token);
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
                    services.AddHttpClient<RemotePlatformAuditRetention>().ConfigurePrimaryHttpMessageHandler(() => web.Server.CreateHandler());
                    services.AddScoped<IAuditRetentionSource>(provider => provider.GetRequiredService<RemotePlatformAuditRetention>());
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
            Assert.AreEqual(4L, preview!.AffectedRecords);
            Assert.AreEqual(1L, preview.AlertEpisodes);
            Assert.AreEqual(1L, preview.Notifications);
            Assert.AreEqual(1L, preview.BackgroundJobs);
            using var updated = await client.PutAsJsonAsync("/internal/v1/history/retention", new RetentionChange(actor, new(30, settings.Version, preview.ConfirmationToken)), token);
            updated.EnsureSuccessStatusCode();
            await using (var scope = operations.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<AuditRetentionCleanup>().RunAsync(token);
            await using var check = await web.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [scheduling].[DashboardAudit] WHERE [Result] = 'started'").SingleAsync(token));
            Assert.AreEqual(1, await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [scheduling].[DashboardAudit] WHERE [Result] = 'accepted'").SingleAsync(token));
            await using var agentCheck = await web.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(0, await agentCheck.Set<AgentSettingAudit>().CountAsync(token));
            await using var securityCheck = await web.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(0, await securityCheck.InventorySettingAudits.CountAsync(token));
            await using var oldOperations = await web.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await oldOperations.Set<PingAlert>().CountAsync(token));
            Assert.AreEqual(0, await oldOperations.Set<Notification>().CountAsync(token));
            Assert.AreEqual(0, await oldOperations.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [operations].[AuditEvents] WHERE [OccurredAt] < DATEADD(day, -30, SYSUTCDATETIME())").SingleAsync(token));
            Assert.AreEqual(1, await oldOperations.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [scheduling].[Outbox] WHERE [State] = 0").SingleAsync(token));
    }
}
