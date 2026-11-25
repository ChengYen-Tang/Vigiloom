extern alias ResourcesHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Groups;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Devices;
using Vigiloom.Web.Groups;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Services;
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
        var auditQueue = "vigiloom-test-audit-" + suffix;
        var observabilityQueue = "vigiloom-test-observation-" + suffix;
        try
        {
            await ReusableWebDatabase.ExecuteAsync(async (webDb, outerToken) =>
            await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (resourceDb, token) =>
            {
                var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(resourceDb.ConnectionString,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
                await using (var db = new ResourcesDbContext(options)) await db.Database.MigrateAsync(token);
                var candidate = new ApplicationCandidate(Guid.CreateVersion7(), "orders", "production", "Orders", "", DateTimeOffset.UtcNow);
                var application = new ApplicationService(candidate, DateTimeOffset.UtcNow);
                await using (var db = new ResourcesDbContext(options)) { db.ApplicationServices.Add(application); await db.SaveChangesAsync(token); }
                WebApplicationFactory<Program>? web = null;
                using var resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.UseSetting("ConnectionStrings:Resources", resourceDb.ConnectionString);
                    builder.UseSetting("ConnectionStrings:Messaging", broker);
                    builder.UseSetting("Messaging:NetworkQueue", networkQueue);
                    builder.UseSetting("Messaging:AgentQueue", agentQueue);
                    builder.UseSetting("Messaging:AuditQueue", auditQueue);
                    builder.UseSetting("Messaging:ObservabilityQueue", observabilityQueue);
                    builder.ConfigureTestServices(services => services.AddHttpClient("ObservationSourceVerifier")
                        .ConfigurePrimaryHttpMessageHandler(() => web!.Server.CreateHandler()));
                });
                using var webHost = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Vigiloom"] = webDb.ConnectionString,
                        ["Services:Resources:Url"] = "http://resources"
                    }));
                    builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteDeviceManagement))
                        .ConfigurePrimaryHttpMessageHandler(() => resources.Server.CreateHandler()));
                    builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteGroupManagement))
                        .ConfigurePrimaryHttpMessageHandler(() => resources.Server.CreateHandler()));
                    builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteServiceMembership))
                        .ConfigurePrimaryHttpMessageHandler(() => resources.Server.CreateHandler()));
                    builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteCandidateAdministration))
                        .ConfigurePrimaryHttpMessageHandler(() => resources.Server.CreateHandler()));
                    builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemotePublicReader))
                        .ConfigurePrimaryHttpMessageHandler(() => resources.Server.CreateHandler()));
                });
                web = webHost;
                await using var scope = webHost.Services.CreateAsyncScope();
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
                var groups = scope.ServiceProvider.GetRequiredService<IGroupManagement>();
                Assert.IsInstanceOfType<RemoteGroupManagement>(groups);
                var group = await groups.CreateAsync(new GroupInput("Remote group", null), Guid.CreateVersion7(), token);
                Assert.AreEqual(GroupResultStatus.Success, group.Status);
                var membership = await groups.ChangeMembersAsync(group.Group!.Id,
                    new GroupMembershipChange(group.Group.Version, [id], []), Guid.CreateVersion7(), token);
                Assert.AreEqual(GroupResultStatus.Success, membership.Status);
                Assert.HasCount(1, (await groups.ListForDeviceAsync(id, 1, token)).Items);
                Assert.HasCount(1, (await groups.ListDevicesAsync(group.Group.Id, false, true, null, 1, token)).Items);
                await using (var db = new ResourcesDbContext(options))
                {
                    (await db.Devices.SingleAsync(x => x.Id == id, token)).SetPublication(true, "Public router", PublicationPolicy.BaselineVersion, DateTimeOffset.UtcNow);
                    (await db.Groups.SingleAsync(x => x.Id == group.Group.Id, token)).SetPublication(true, "Public group", PublicationPolicy.BaselineVersion, DateTimeOffset.UtcNow);
                    await db.SaveChangesAsync(token);
                }
                using var anonymous = webHost.CreateClient();
                var publicDevices = await anonymous.GetFromJsonAsync<PublicPage<PublicDevice>>("/api/v1/public/devices", token);
                Assert.AreEqual("Public router", publicDevices?.Items.Single().Name);
                var publicGroups = await anonymous.GetFromJsonAsync<PublicPage<PublicGroup>>("/api/v1/public/groups", token);
                Assert.AreEqual(1, publicGroups?.Items.Single().DeviceCount);
                var body = await anonymous.GetStringAsync($"/api/v1/public/devices/{id:D}", token);
                Assert.DoesNotContain("changed.example", body);
                await using (var db = new ResourcesDbContext(options))
                {
                    (await db.Devices.SingleAsync(x => x.Id == id, token)).SetPublication(false, null, null, DateTimeOffset.UtcNow);
                    await db.SaveChangesAsync(token);
                }
                using var revoked = await anonymous.GetAsync($"/api/v1/public/devices/{id:D}", token);
                Assert.AreEqual(HttpStatusCode.NotFound, revoked.StatusCode);
                Assert.IsEmpty((await anonymous.GetFromJsonAsync<PublicPage<PublicGroup>>("/api/v1/public/groups", token))!.Items);
                var services = scope.ServiceProvider.GetRequiredService<IServiceMembership>();
                Assert.IsInstanceOfType<RemoteServiceMembership>(services);
                var serviceMembership = await services.ChangeAsync(group.Group.Id,
                    new GroupMembershipChange((await groups.GetAsync(group.Group.Id, token))!.Version, [application.Id], []), Guid.CreateVersion7(), token);
                Assert.AreEqual(GroupResultStatus.Success, serviceMembership.Status);
                Assert.HasCount(1, (await services.ListAsync(group.Group.Id, false, true, null, 1, token)).Items);
                Assert.HasCount(1, (await services.ListGroupsAsync(application.Id, 1, token)).Items);
                var telemetrySource = new ServiceSource("Receiver", new byte[32], DateTimeOffset.UtcNow);
                var sourceContexts = webHost.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
                await using (var db = await sourceContexts.CreateDbContextAsync(token))
                {
                    db.Sources.Add(telemetrySource);
                    await db.SaveChangesAsync(token);
                }
                var candidatesApi = scope.ServiceProvider.GetRequiredService<ICandidateAdministration>();
                Assert.IsInstanceOfType<RemoteCandidateAdministration>(candidatesApi);
                var authenticated = new AuthenticatedSource(telemetrySource.Id, telemetrySource.Version);
                Assert.AreEqual(CandidateObservation.Recorded, await candidatesApi.ObserveAsync(authenticated, "new-api", "production", "New API", token, ""));
                var candidates = await candidatesApi.ListAsync(1, token);
                Assert.HasCount(1, candidates);
                Assert.AreEqual("Receiver", candidates[0].SourceName);
                Assert.AreEqual(ApprovalStatus.Approved, (await candidatesApi.ApproveAsync(candidates[0].Id, Guid.CreateVersion7(), token)).Status);
                Assert.AreEqual(CandidateObservation.Recorded, await candidatesApi.ObserveAsync(authenticated, "other-api", "production", "Other API", token, ""));
                var other = (await candidatesApi.ListAsync(1, token)).Single();
                await using (var db = await sourceContexts.CreateDbContextAsync(token))
                {
                    (await db.Sources.SingleAsync(s => s.Id == telemetrySource.Id, token)).Revoke(DateTimeOffset.UtcNow);
                    await db.SaveChangesAsync(token);
                }
                Assert.IsEmpty(await candidatesApi.ListAsync(1, token));
                Assert.AreEqual(ApprovalStatus.SourceUnavailable, (await candidatesApi.ApproveAsync(other.Id, Guid.CreateVersion7(), token)).Status);
                var archived = await devices.SetArchivedAsync(id, true, (await devices.GetAsync(id, token))!.Version, Guid.CreateVersion7(), token);
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
            await channel.QueueDeleteAsync(auditQueue, cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(observabilityQueue, cancellationToken: CancellationToken.None);
        }
    }
}
