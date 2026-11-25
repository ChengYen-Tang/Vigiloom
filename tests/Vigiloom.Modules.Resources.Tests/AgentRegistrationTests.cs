using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Messaging;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.Resources.Application.AgentIdentity;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Wolverine;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentRegistrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task EnrollmentAuthenticatesAndRevocationTakesEffect()
    {
        var broker = Environment.GetEnvironmentVariable("VIGILOOM_TEST_RABBITMQ");
        if (string.IsNullOrWhiteSpace(broker)) Assert.Inconclusive("Set VIGILOOM_TEST_RABBITMQ for the RabbitMQ integration test.");
        var suffix = Guid.CreateVersion7().ToString("N");
        var agentQueue = "vigiloom-test-agent-registration-" + suffix;
        var auditQueue = "vigiloom-test-agent-audit-" + suffix;
        try
        {
            await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Services.AddModuleSqlServer<ResourcesDbContext>(database.ConnectionString, "resources");
                builder.Services.AddSingleton(TimeProvider.System);
                builder.Services.AddScoped<AgentRegistration>();
                builder.UseWolverine(options =>
                {
                    options.UseVigiloomMessaging(database.ConnectionString, broker!);
                    options.Discovery.DisableConventionalDiscovery();
                    options.PublishReliablyTo<AgentRegistrationChanged>(agentQueue);
                    options.PublishReliablyTo<AgentRegistrationAudit>(auditQueue);
                });
                using var host = builder.Build();
                var contexts = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
                var device = new Device("Server", "server.example", true, DateTimeOffset.UtcNow);
                await using (var db = await contexts.CreateDbContextAsync(token))
                {
                    await db.Database.MigrateAsync(token);
                    db.Devices.Add(device);
                    await db.SaveChangesAsync(token);
                }
                await host.StartAsync(token);
                try
                {
                    AgentEnrollmentIssue issue;
                    await using (var scope = host.Services.CreateAsyncScope())
                        issue = await scope.ServiceProvider.GetRequiredService<AgentRegistration>().IssueAsync(device.Id, Guid.CreateVersion7(), token);
                    Assert.AreEqual(AgentRegistrationStatus.Success, issue.Status);
                    var machineSecret = RandomNumberGenerator.GetBytes(32);
                    var verifier = SHA256.HashData(machineSecret);
                    AgentEnrollmentResult result;
                    await using (var scope = host.Services.CreateAsyncScope())
                        result = await scope.ServiceProvider.GetRequiredService<AgentRegistration>().RedeemAsync(issue.Secret, verifier, token);
                    Assert.AreEqual(AgentRegistrationStatus.Success, result.Status);
                    Assert.IsNotNull(result.AgentId);
                    var credential = AgentCredentials.Encode("vla", result.AgentId.Value, machineSecret);
                    await using (var scope = host.Services.CreateAsyncScope())
                    {
                        var agents = scope.ServiceProvider.GetRequiredService<AgentRegistration>();
                        Assert.AreEqual(device.Id, (await agents.AuthenticateAsync(credential, token))?.DeviceId);
                        Assert.AreEqual(AgentRegistrationStatus.Success, await agents.RevokeAsync(result.AgentId.Value, Guid.CreateVersion7(), token));
                        Assert.IsNull(await agents.AuthenticateAsync(credential, token));
                    }
                }
                finally { await host.StopAsync(CancellationToken.None); }
            }, TestContext.CancellationToken);
        }
        finally
        {
            var factory = new ConnectionFactory { Uri = new Uri(broker!) };
            await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(agentQueue, cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(auditQueue, cancellationToken: CancellationToken.None);
        }
    }
}
