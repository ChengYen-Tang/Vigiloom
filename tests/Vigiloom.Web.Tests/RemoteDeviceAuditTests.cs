extern alias ResourcesHost;

using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Diagnostics;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class RemoteDeviceAuditTests(TestContext context)
{
    [TestMethod]
    public Task DeviceAuditUsesResourcesDatabase() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (legacy, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (resources, token) =>
        {
            var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(resources.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            var now = DateTimeOffset.UtcNow;
            var actor = Guid.CreateVersion7();
            var device = new Device("Test", "127.0.0.1", false, now);
            await using (var db = new ResourcesDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Devices.Add(device);
                db.DeviceAudits.Add(new DeviceAudit(device.Id, actor, "created", now));
                await db.SaveChangesAsync(token);
            }
            using var resourceHost = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Resources"] = resources.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    foreach (var item in services.Where(x => x.ImplementationType?.Name == "WorkDispatcher").ToArray()) services.Remove(item);
                });
            });
            using var resourceClient = resourceHost.CreateClient();
            using var webHost = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Vigiloom"] = legacy.ConnectionString, ["Services:Resources:Url"] = "http://resources", ["Services:Observability:Url"] = "http://observability" }));
                web.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteDeviceAuditFeed))
                    .ConfigurePrimaryHttpMessageHandler(() => resourceHost.Server.CreateHandler()));
            });
            await using var scope = webHost.Services.CreateAsyncScope();
            var feed = scope.ServiceProvider.GetRequiredService<AuditFeedReader>();
            var page = await feed.QueryAsync(new ClaimsPrincipal(), AuditFeedSource.Devices,
                new(now.AddMinutes(-1), now.AddMinutes(1)), token);
            var entry = Assert.ContainsSingle(page.Items);
            Assert.AreEqual(device.Id, entry.SubjectId);
            Assert.AreEqual(actor, entry.ActorId);
            Assert.AreEqual(AuditFeedAction.DeviceCreated, entry.Action);
        }, outerToken), context.CancellationToken);
}
