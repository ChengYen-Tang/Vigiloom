using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.Resources.Application.AgentIdentity;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

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
        {
            await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Services.AddModuleSqlServer<ResourcesDbContext>(database.ConnectionString, "resources");
                builder.Services.AddSingleton(TimeProvider.System);
                builder.Services.AddScoped<AgentRegistration>();
                builder.Services.AddScoped<IOutboxHandler, TestTemporalOutboxHandler>();
                builder.Services.AddScoped<OutboxWriter>();
                builder.Services.AddScoped<TemporalWorkWriter>();
                builder.Services.AddScoped<ResourceWork>();
                using var host = builder.Build();
                var contexts = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
                var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
                await using (var scheduling = new SchedulingDbContext(schedulingOptions)) await scheduling.Database.MigrateAsync(token);
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
                    Assert.IsTrue(AgentCredentials.TryDecode(issue.Secret, "vle", out var enrollmentId, out var enrollmentBytes));
                    CryptographicOperations.ZeroMemory(enrollmentBytes);
                    var proof = AgentCredentials.Encode("vlr", enrollmentId, machineSecret);
                    await using (var scope = host.Services.CreateAsyncScope())
                        Assert.AreEqual(result.AgentId, (await scope.ServiceProvider.GetRequiredService<AgentRegistration>().RecoverAsync(issue.Secret, proof, token)).AgentId);
                    var replacementSecret = RandomNumberGenerator.GetBytes(32);
                    var replacement = AgentCredentials.Encode("vla", result.AgentId.Value, replacementSecret);
                    await using (var scope = host.Services.CreateAsyncScope())
                        Assert.AreEqual(AgentRegistrationStatus.Success, await scope.ServiceProvider.GetRequiredService<AgentRegistration>()
                            .BeginRotationAsync(credential, SHA256.HashData(replacementSecret), token));
                    await using (var scope = host.Services.CreateAsyncScope())
                        Assert.AreEqual(AgentRegistrationStatus.Success, await scope.ServiceProvider.GetRequiredService<AgentRegistration>().FinishRotationAsync(replacement, token));
                    await using (var scope = host.Services.CreateAsyncScope())
                    {
                        var agents = scope.ServiceProvider.GetRequiredService<AgentRegistration>();
                        Assert.IsNull(await agents.AuthenticateAsync(credential, token));
                        Assert.AreEqual(device.Id, (await agents.AuthenticateAsync(replacement, token))?.DeviceId);
                        Assert.AreEqual(AgentRegistrationStatus.Success, await agents.RevokeAsync(result.AgentId.Value, Guid.CreateVersion7(), token));
                        Assert.IsNull(await agents.AuthenticateAsync(replacement, token));
                    }
                    await using (var scope = host.Services.CreateAsyncScope())
                        Assert.AreEqual(AgentRegistrationStatus.Success, await scope.ServiceProvider.GetRequiredService<AgentRegistration>()
                            .RevokeEnrollmentAsync(enrollmentId, Guid.CreateVersion7(), token));
                }
                finally { await host.StopAsync(CancellationToken.None); }
            }, TestContext.CancellationToken);
        }

    }
}
