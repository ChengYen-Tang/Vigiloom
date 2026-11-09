using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Api;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentReplayHttpTests(TestContext context)
{
    [TestMethod]
    public Task Receiver_EnforcesWindowAndRetainsRejectedAgentBatches() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }));
            });
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var device = new Device("replay test", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            EnrollmentIssue issued;
            await using (var scope = factory.Services.CreateAsyncScope())
                issued = await scope.ServiceProvider.GetRequiredService<AgentIdentity>().IssueAsync(device.Id, Guid.CreateVersion7(), token);
            var root = Path.Combine(Path.GetTempPath(), "VigiloomReplayHttpTests", Path.GetRandomFileName());
            try
            {
                var handler = new CaptureBatchStatus(factory.Server.CreateHandler());
                using var http = new HttpClient(handler);
                var vault = new IdentityVault(root);
                var server = new Uri("https://localhost/");
                var agentId = await new IdentityClient(http).EnrollAsync(vault, server, issued.Secret, token);
                var queue = new DurableBatchQueue(vault, agentId);
                var sender = new AgentBatchClient(http, TimeProvider.System);
                var now = DateTimeOffset.UtcNow;
                AgentBatch CreateBatch(DateTimeOffset enqueued) => new(1, Guid.CreateVersion7(), enqueued,
                    [new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "test", "X64", ".NET 10")]);
                var original = CreateBatch(now.AddDays(-2));
                var originalBytes = AgentBatchCodec.Serialize(original);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(original.BatchId, original.EnqueuedAt, 1, originalBytes, token));
                Assert.AreEqual(BatchSendResult.Delivered, (await sender.SendNextAsync(vault, server, token)).Status);
                await using (var db = await agents.CreateDbContextAsync(token))
                    await db.Database.ExecuteSqlRawAsync("DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'); UPDATE [agent].[ReplayPolicies] SET [Phase] = 2, [FreshInstallation] = 1, [ActivatedAt] = DATEADD(hour, -1, @now), [EnforcedAt] = DATEADD(hour, -1, @now) WHERE [Scope] = 1", token);
                var expired = CreateBatch(now.AddDays(-2));
                var expiredBytes = AgentBatchCodec.Serialize(expired);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(expired.BatchId, expired.EnqueuedAt, 1, expiredBytes, token));
                Assert.AreEqual(BatchSendResult.Rejected, (await sender.SendNextAsync(vault, server, token)).Status);
                Assert.AreEqual(HttpStatusCode.Gone, handler.LastBatchStatus);
                Assert.AreEqual(1L, (await queue.GetStatusAsync(token)).QuarantinedBatches);
                using (var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
                {
                    sqlite.Open(); using var command = sqlite.CreateCommand();
                    command.CommandText = "SELECT payload, rejection FROM batches WHERE batch_id = $id";
                    command.Parameters.AddWithValue("$id", expired.BatchId.ToString("D"));
                    using var row = command.ExecuteReader(); Assert.IsTrue(row.Read());
                    CollectionAssert.AreEqual(expiredBytes, (byte[])row[0]);
                    Assert.AreEqual((int)QueueRejection.Expired, row.GetInt32(1));
                }
                await using var scope = factory.Services.CreateAsyncScope();
                var monitor = scope.ServiceProvider.GetRequiredService<AgentMonitoring>();
                Assert.AreEqual(AgentReplayIssue.Expired, (await monitor.GetAsync(device.Id, token))!.LastReplayIssue!.Reason);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(original.BatchId, original.EnqueuedAt, 1, originalBytes, token));
                Assert.AreEqual(BatchSendResult.Delivered, (await sender.SendNextAsync(vault, server, token)).Status);
                Assert.AreEqual(AgentReplayIssue.Expired, (await monitor.GetAsync(device.Id, token))!.LastReplayIssue!.Reason);

                await using (var db = await agents.CreateDbContextAsync(token))
                {
                    var stored = await db.ReceivedBatches.SingleAsync(token);
                    Assert.IsNotNull(stored.TrustedReceivedAt);
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [ReplayCleanedBefore] = DATEADD(minute, -30, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [Id] = {agentId}", token);
                }
                // A retained duplicate is still acknowledged before the cleanup fence is checked.
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(original.BatchId, original.EnqueuedAt, 1, originalBytes, token));
                Assert.AreEqual(BatchSendResult.Delivered, (await sender.SendNextAsync(vault, server, token)).Status);
                var cleanedRange = CreateBatch(now.AddHours(-1));
                var cleanedBytes = AgentBatchCodec.Serialize(cleanedRange);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(cleanedRange.BatchId, cleanedRange.EnqueuedAt, 1, cleanedBytes, token));
                Assert.AreEqual(BatchSendResult.Rejected, (await sender.SendNextAsync(vault, server, token)).Status);
                Assert.AreEqual(HttpStatusCode.Gone, handler.LastBatchStatus);

                var future = CreateBatch(now.AddMinutes(10));
                var futureBytes = AgentBatchCodec.Serialize(future);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(future.BatchId, future.EnqueuedAt, 1, futureBytes, token));
                Assert.AreEqual(BatchSendResult.Retry, (await sender.SendNextAsync(vault, server, token)).Status);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, handler.LastBatchStatus);
                CollectionAssert.AreEqual(futureBytes, (await queue.PeekAsync(token))!.Payload);
                Assert.AreEqual(AgentReplayIssue.TimestampAhead, (await monitor.GetAsync(device.Id, token))!.LastReplayIssue!.Reason);
                await using var verify = await agents.CreateDbContextAsync(token);
                Assert.AreEqual(1, await verify.ReceivedBatches.CountAsync(token));
                Assert.AreEqual(1, await verify.RuntimeObservations.CountAsync(token));
            }
            finally
            {
                var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomReplayHttpTests")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }, context.CancellationToken);

    private sealed class CaptureBatchStatus(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public HttpStatusCode? LastBatchStatus { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri?.AbsolutePath == "/api/v1/agent/batches") LastBatchStatus = response.StatusCode;
            return response;
        }
    }
}
