using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Groups;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Messaging;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Wolverine;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DeviceChangeMessagingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CommittedDeviceIsSentToNetworkAndAgentServices()
    {
        var broker = Environment.GetEnvironmentVariable("VIGILOOM_TEST_RABBITMQ");
        if (string.IsNullOrWhiteSpace(broker)) Assert.Inconclusive("Set VIGILOOM_TEST_RABBITMQ for the RabbitMQ integration test.");
        var suffix = Guid.CreateVersion7().ToString("N");
        var networkQueue = "vigiloom-test-network-" + suffix;
        var agentQueue = "vigiloom-test-agent-" + suffix;
        var auditQueue = "vigiloom-test-audit-" + suffix;
        var observabilityQueue = "vigiloom-test-observation-" + suffix;
        var sources = new TestSources();
        try
        {
            await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Services.AddModuleSqlServer<ResourcesDbContext>(database.ConnectionString, "resources");
                builder.Services.AddSingleton(TimeProvider.System);
                builder.Services.AddScoped<DeviceManagement>();
                builder.Services.AddScoped<DeviceChangeOutbox>();
                builder.Services.AddScoped<GroupManagement>();
                builder.Services.AddScoped<GroupAuditOutbox>();
                builder.Services.AddSingleton<IApplicationSourceVerifier>(sources);
                builder.Services.AddScoped<ApplicationDiscovery>();
                builder.Services.AddScoped<ApplicationApproval>();
                builder.UseWolverine(options =>
                {
                    options.UseVigiloomMessaging(database.ConnectionString, broker!);
                    options.Discovery.DisableConventionalDiscovery();
                    options.PublishReliablyTo<NetworkDeviceChanged>(networkQueue);
                    options.PublishReliablyTo<AgentDeviceChanged>(agentQueue);
                    options.PublishReliablyTo<GroupAuditRecorded>(auditQueue);
                    options.PublishReliablyTo<ApplicationApprovalAudit>(auditQueue);
                    options.PublishReliablyTo<ApplicationApproved>(observabilityQueue);
                });
                using var host = builder.Build();
                await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
                    await db.Database.MigrateAsync(token);
                await host.StartAsync(token);
                try
                {
                    Guid id;
                    await using (var scope = host.Services.CreateAsyncScope())
                    {
                        var result = await scope.ServiceProvider.GetRequiredService<DeviceManagement>()
                            .CreateAsync(new DeviceInput("Host", "host.example", [], true), Guid.CreateVersion7(), token);
                        Assert.AreEqual(DeviceResultStatus.Success, result.Status);
                        id = result.Device!.Id;
                    }
                    var factory = new ConnectionFactory { Uri = new Uri(broker!) };
                    await using var connection = await factory.CreateConnectionAsync(token);
                    await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    foreach (var queue in new[] { networkQueue, agentQueue })
                    {
                        BasicGetResult? message;
                        while ((message = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken: timeout.Token)) is null)
                            await Task.Delay(100, timeout.Token);
                        Assert.Contains(id.ToString("D"), System.Text.Encoding.UTF8.GetString(message.Body.Span));
                    }
                    Guid groupId;
                    await using (var scope = host.Services.CreateAsyncScope())
                    {
                        var groups = scope.ServiceProvider.GetRequiredService<GroupManagement>();
                        var created = await groups.CreateAsync(new GroupInput("Servers", null), Guid.CreateVersion7(), token);
                        Assert.AreEqual(GroupResultStatus.Success, created.Status);
                        groupId = created.Group!.Id;
                    }
                    BasicGetResult? groupMessage;
                    while ((groupMessage = await channel.BasicGetAsync(auditQueue, autoAck: true, cancellationToken: timeout.Token)) is null)
                        await Task.Delay(100, timeout.Token);
                    Assert.Contains(groupId.ToString("D"), System.Text.Encoding.UTF8.GetString(groupMessage.Body.Span));
                    await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
                        Assert.IsTrue(await db.Groups.AnyAsync(x => x.Id == groupId, token));
                    Guid candidateId;
                    await using (var scope = host.Services.CreateAsyncScope())
                    {
                        var discovery = scope.ServiceProvider.GetRequiredService<ApplicationDiscovery>();
                        var observation = new ApplicationObservation(Guid.CreateVersion7(), "orders", "production", "Orders", "");
                        await discovery.ObserveAsync(observation, token);
                        candidateId = (await discovery.ListAsync(1, token)).Single().Id;
                        sources.Active = false;
                        var denied = await scope.ServiceProvider.GetRequiredService<ApplicationApproval>().ApproveAsync(candidateId, Guid.CreateVersion7(), token);
                        Assert.AreEqual(ApplicationApprovalStatus.SourceUnavailable, denied.Status);
                    }
                    sources.Active = true;
                    Guid serviceId;
                    await using (var scope = host.Services.CreateAsyncScope())
                    {
                        var approval = await scope.ServiceProvider.GetRequiredService<ApplicationApproval>().ApproveAsync(candidateId, Guid.CreateVersion7(), token);
                        Assert.AreEqual(ApplicationApprovalStatus.Approved, approval.Status);
                        serviceId = approval.ServiceId!.Value;
                    }
                    foreach (var queue in new[] { auditQueue, observabilityQueue })
                    {
                        BasicGetResult? message;
                        while ((message = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken: timeout.Token)) is null)
                            await Task.Delay(100, timeout.Token);
                        Assert.Contains(serviceId.ToString("D"), System.Text.Encoding.UTF8.GetString(message.Body.Span));
                    }
                    var rolledBack = new Device("Rollback", "rollback.example", true, DateTimeOffset.UtcNow);
                    await using (var scope = host.Services.CreateAsyncScope())
                    await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
                    await using (var tx = await db.Database.BeginTransactionAsync(token))
                    {
                        db.Devices.Add(rolledBack);
                        await db.SaveChangesAsync(token);
                        db.DeviceGroups.Add(new DeviceGroup(Guid.CreateVersion7(), rolledBack.Id));
                        await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<DeviceChangeOutbox>().CommitAsync(db, rolledBack, token));
                    }
                    await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
                        Assert.IsFalse(await db.Devices.AnyAsync(x => x.Id == rolledBack.Id, token));
                    Assert.IsNull(await channel.BasicGetAsync(networkQueue, autoAck: true, cancellationToken: token));
                    Assert.IsNull(await channel.BasicGetAsync(agentQueue, autoAck: true, cancellationToken: token));
                }
                finally { await host.StopAsync(CancellationToken.None); }
            }, TestContext.CancellationToken);
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

    private sealed class TestSources : IApplicationSourceVerifier
    {
        public bool Active { get; set; } = true;
        public Task<bool> IsActiveAsync(Guid sourceId, CancellationToken token) => Task.FromResult(Active);
    }
}
