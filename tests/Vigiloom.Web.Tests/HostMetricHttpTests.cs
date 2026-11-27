using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class HostMetricHttpTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public async Task HostMetrics_RealMemoryTravelsThroughQueueAndAuthenticatedReceiver()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomMetricTransportTests", Path.GetRandomFileName());
        try
        {
            await ReusableWebDatabase.ExecuteAsync( async (database, token) =>
            {
                using var factory = PingTestSite.CreateFactory(database.ConnectionString);
                await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
                var now = DateTimeOffset.UtcNow;
                var device = new Device("metric transport", "127.0.0.1", true, now);
                var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
                await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
                var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
                var secret = RandomNumberGenerator.GetBytes(32);
                Guid agentId;
                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                    var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
                    var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
                    agentId = registered.AgentId!.Value;
                }
                ulong total;
                if (OperatingSystem.IsWindows()) total = Vigiloom.Agent.Platforms.Windows.Metrics.MemoryReader.Read().TotalBytes;
                else if (OperatingSystem.IsLinux()) total = (await Vigiloom.Agent.Platforms.Linux.Metrics.MemoryReader.ReadAsync(token))!.Value.TotalBytes;
                else throw new PlatformNotSupportedException();
                Assert.IsGreaterThan(0UL, total);
                var server = new Uri("https://localhost/");
                var vault = new IdentityVault(root);
                await using (var session = vault.Acquire())
                    await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = agentId, MachineSecret = WebEncoders.Base64UrlEncode(secret) }, token);
                var point = new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.MemoryTotalBytes, "test.host.memory", null, total);
                var unavailable = new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CollectionUnavailable, "agent.collector", "cpu", 1);
                var batch = new AgentMetricBatch(2, Guid.CreateVersion7(), now, [point, unavailable]);
                var bytes = AgentMetricBatchCodec.Serialize(batch);
                var queue = new DurableBatchQueue(vault, agentId);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, now, 2, bytes, token));
                using var agentClient = PingTestSite.Client(factory);
                Assert.AreEqual(BatchSendResult.Delivered, (await new AgentBatchClient(agentClient, TimeProvider.System).SendNextAsync(vault, server, token)).Status);
                Assert.IsNull(await queue.PeekAsync(token));
                await using var readScope = factory.Services.CreateAsyncScope();
                var history = readScope.ServiceProvider.GetRequiredService<IHostMetricHistory>();
                var result = await history.GetAsync(device.Id, now.AddMinutes(-1), now.AddMinutes(1), null, null, token);
                Assert.IsNotNull(result);
                Assert.HasCount(2, result.Items);
                Assert.IsTrue(result.Items.All(x => x.AgentId == agentId));
                Assert.AreEqual((double)total, result.Items.Single(x => x.Kind == AgentMetricKind.MemoryTotalBytes).Value);
                var marker = result.Items.Single(x => x.Kind == AgentMetricKind.CollectionUnavailable);
                Assert.AreEqual("cpu", marker.Dimension);
                Assert.AreEqual(1d, marker.Value);
            }, TestContext.CancellationToken);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomMetricTransportTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
