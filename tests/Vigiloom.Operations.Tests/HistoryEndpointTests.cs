using System.Net;
using System.Net.Http.Json;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HistoryEndpointTests(TestContext context)
{
    [TestMethod]
    public Task HistoryCanBeQueriedAndRetentionKeepsOpenAlerts() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(database.ConnectionString, "operations");
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            await using (var db = await factory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
            var old = DateTimeOffset.UtcNow.AddDays(-400);
            var open = new PingAlert { DeviceId = Guid.CreateVersion7(), Target = "open.example", OpenedAt = old };
            var closed = new PingAlert { DeviceId = Guid.CreateVersion7(), Target = "closed.example", OpenedAt = old, RecoveredAt = old };
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                db.Set<PingAlert>().AddRange(open, closed);
                db.Set<Notification>().Add(new Notification { MessageId = Guid.CreateVersion7(), Source = "ping", Title = "Old alert", Detail = "Details", OccurredAt = old });
                await db.SaveChangesAsync(token);
            }
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = database.ConnectionString }));
                web.ConfigureTestServices(s =>
                {
                    HostWorkers.RemoveTemporal(s);
                    foreach (var name in new[] { "WorkDispatcher", "LineAggregationWorker" })
                    {
                        var worker = s.SingleOrDefault(item => item.ImplementationType?.Name == name);
                        if (worker is not null) s.Remove(worker);
                    }
                    s.RemoveAll<IDataProtectionProvider>();
                    s.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                });
            });
            using var client = host.CreateClient();
            var alerts = await client.GetFromJsonAsync<AlertEpisodePage>("/internal/v1/history/alerts", token);
            Assert.HasCount(2, alerts!.Items);
            var notices = await client.GetFromJsonAsync<NotificationPage>("/internal/v1/history/notifications?source=ping", token);
            Assert.AreEqual("Old alert", notices!.Items.Single().Title);
            using (var invalid = await client.GetAsync("/internal/v1/history/alerts?start=2026-02-01&end=2026-01-01", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            var settings = await client.GetFromJsonAsync<AuditRetentionSnapshot>("/internal/v1/history/retention", token);
            Assert.IsNotNull(settings);
            var actor = Guid.CreateVersion7();
            using var previewResponse = await client.PostAsJsonAsync("/internal/v1/history/retention/preview",
                new RetentionPreviewChange(actor, new(30, settings.Version)), token);
            var preview = await previewResponse.Content.ReadFromJsonAsync<AuditRetentionPreview>(token);
            Assert.IsNotNull(preview);
            Assert.AreEqual(1, preview.AlertEpisodes);
            Assert.AreEqual(1, preview.Notifications);
            using var update = await client.PutAsJsonAsync("/internal/v1/history/retention",
                new RetentionChange(actor, new(30, settings.Version, preview.ConfirmationToken)), token);
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
            // Run a regular scheduler against the shared storage; the Operations worker only consumes its queue.
            await using (var schedulingDb = await factory.CreateDbContextAsync(token))
            {
                var previous = DateTime.UtcNow.AddHours(-2).ToString("O");
                await schedulingDb.Database.ExecuteSqlInterpolatedAsync($"""
                    IF EXISTS (SELECT 1 FROM [hangfire].[Hash] WHERE [Key] = 'recurring-job:vigiloom-operations-retention' AND [Field] = 'LastExecution')
                        UPDATE [hangfire].[Hash] SET [Value] = {previous} WHERE [Key] = 'recurring-job:vigiloom-operations-retention' AND [Field] = 'LastExecution';
                    ELSE
                        INSERT INTO [hangfire].[Hash] ([Key], [Field], [Value]) VALUES ('recurring-job:vigiloom-operations-retention', 'LastExecution', {previous});
                    UPDATE [hangfire].[Set] SET Score = 0 WHERE [Key] = 'recurring-jobs' AND Value = 'vigiloom-operations-retention';
                    """, token);
            }
            using var scheduler = new BackgroundJobServer(new BackgroundJobServerOptions
            { Queues = ["scheduler"], SchedulePollingInterval = TimeSpan.FromSeconds(1) }, host.Services.GetRequiredService<JobStorage>());
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await using var check = await factory.CreateDbContextAsync(token);
                if (!await check.Set<Notification>().AnyAsync(token)) break;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            await using var result = await factory.CreateDbContextAsync(token);
            Assert.AreEqual(open.Id, (await result.Set<PingAlert>().SingleAsync(token)).Id);
            Assert.AreEqual(0, await result.Set<Notification>().CountAsync(token));
        }, context.CancellationToken);
}
