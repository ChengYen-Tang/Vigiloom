extern alias ResourcesHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Devices;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class RemoteDeviceManagementTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task WebUsesIndependentResourceServiceForDeviceChanges()
    {
        var broker = Environment.GetEnvironmentVariable("VIGILOOM_TEST_RABBITMQ");
        if (string.IsNullOrWhiteSpace(broker)) Assert.Inconclusive("Set VIGILOOM_TEST_RABBITMQ for the RabbitMQ integration test.");
        var suffix = Guid.CreateVersion7().ToString("N");
        var networkQueue = "vigiloom-test-network-" + suffix;
        var agentQueue = "vigiloom-test-agent-" + suffix;
        try
        {
            await ReusableWebDatabase.ExecuteAsync(async (webDb, outerToken) =>
            await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (resourceDb, token) =>
            {
                var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(resourceDb.ConnectionString,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
                await using (var db = new ResourcesDbContext(options)) await db.Database.MigrateAsync(token);
                using var resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.UseSetting("ConnectionStrings:Resources", resourceDb.ConnectionString);
                    builder.UseSetting("ConnectionStrings:Messaging", broker);
                    builder.UseSetting("Messaging:NetworkQueue", networkQueue);
                    builder.UseSetting("Messaging:AgentQueue", agentQueue);
                });
                using var web = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Vigiloom"] = webDb.ConnectionString,
                        ["Services:Resources:Url"] = "http://resources"
                    }));
                    builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteDeviceManagement))
                        .ConfigurePrimaryHttpMessageHandler(() => resources.Server.CreateHandler()));
                });
                await using var scope = web.Services.CreateAsyncScope();
                var devices = scope.ServiceProvider.GetRequiredService<IDeviceManagement>();
                Assert.IsInstanceOfType<RemoteDeviceManagement>(devices);
                var result = await devices.CreateAsync(new DeviceInput("Remote", "remote.example", [], true), Guid.CreateVersion7(), token);
                Assert.AreEqual(DeviceResultStatus.Success, result.Status);
                var id = result.Device!.Id;
                Assert.AreEqual("Remote", (await devices.GetAsync(id, token))?.Name);
                Assert.HasCount(1, await devices.FindAddressMatchesAsync(new[] { "remote.example" }, null, token));
                var updated = await devices.UpdateAsync(id, new DeviceInput("Renamed", "changed.example", [], true, Version: result.Device.Version), Guid.CreateVersion7(), token);
                Assert.AreEqual(DeviceResultStatus.Success, updated.Status);
                Assert.AreEqual("changed.example", (await devices.GetAsync(id, token))?.Address);
                var archived = await devices.SetArchivedAsync(id, true, updated.Device!.Version, Guid.CreateVersion7(), token);
                Assert.AreEqual(DeviceResultStatus.Success, archived.Status);
                Assert.IsTrue((await devices.GetAsync(id, token))!.Archived);
                await using (var db = new ResourcesDbContext(options)) Assert.IsTrue(await db.Devices.AnyAsync(x => x.Id == id, token));
                var webOptions = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(webDb.ConnectionString).Options;
                await using (var db = new ResourcesDbContext(webOptions)) Assert.IsFalse(await db.Devices.AnyAsync(x => x.Id == id, token));
            }, outerToken), TestContext.CancellationToken);
        }
        finally
        {
            var factory = new ConnectionFactory { Uri = new Uri(broker!) };
            await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(networkQueue, cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(agentQueue, cancellationToken: CancellationToken.None);
        }
    }
}
