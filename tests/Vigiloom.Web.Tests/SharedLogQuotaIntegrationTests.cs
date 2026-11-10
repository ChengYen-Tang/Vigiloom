using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SharedLogQuotaIntegrationTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task AgentAndOtlp_ShareCapacityAcrossRestartAndPolicyChanges() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"vigiloom-quota-{Guid.CreateVersion7():N}.log");
            var statePath = Path.Combine(Path.GetTempPath(), $"vigiloom-quota-agent-{Guid.CreateVersion7():N}");
            try
            {
                await File.WriteAllTextAsync(path, "host quota event\n", token);
                var reading = await new TextFileLogReader(path, AgentTextLogMode.SingleLine, TimeProvider.System).ReadAsync(null, token);
                Assert.AreEqual(LogReadStatus.Record, reading.Status);
                var now = DateTimeOffset.UtcNow;
                var batch = new AgentLogBatch(3, Guid.CreateVersion7(), now, Guid.CreateVersion7(), AgentLogSourceKind.TextFile,
                    "quota-test", 1, [new(Guid.CreateVersion7(), now, reading.Record!.Content, null, null, null, null, null)]);
                var hostPayload = AgentLogBatchCodec.Serialize(batch);
                var otlpPayload = new ExportLogsServiceRequest { ResourceLogs = { new ResourceLogs
                {
                    Resource = new Resource { Attributes = {
                        new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "quota-service" } },
                        new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } } } },
                    ScopeLogs = { new ScopeLogs { LogRecords = { new LogRecord { Body = new AnyValue { StringValue = "application quota event" } } } } }
                } } }.ToByteArray();

                string appSecret;
                Guid agentId;
                bool firstHostDelivered;
                var server = new Uri("https://localhost/");
                var vault = new IdentityVault(statePath);
                using (var factory = PingTestSite.CreateFactory(database.ConnectionString))
                {
                    await PingTestSite.InitializeAsync(factory, token);
                    var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
                    var services = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
                    var quota = factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>();
                    await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
                    await using (var db = await services.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
                    await using (var db = await quota.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
                    var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
                    var device = new Device("quota-host", "127.0.0.1", true, now);
                    await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
                    await using (var scope = factory.Services.CreateAsyncScope())
                    {
                        var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
                        var secret = RandomNumberGenerator.GetBytes(32);
                        var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
                        Assert.IsNotNull(registered.AgentId);
                        agentId = registered.AgentId.Value;
                        await using (var lease = vault.Acquire())
                            await lease.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = agentId, MachineSecret = WebEncoders.Base64UrlEncode(secret) }, token);
                        var source = await scope.ServiceProvider.GetRequiredService<SourceCredentials>().IssueAsync("quota-source", token);
                        appSecret = source.Secret;
                        var discovery = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
                        Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "quota-service", "prod", "Quota", token));
                        var candidate = (await discovery.ListAsync(1, token)).Single(x => x.SourceId == source.SourceId);
                        Assert.AreEqual(ApprovalStatus.Approved, (await scope.ServiceProvider.GetRequiredService<ServiceApproval>()
                            .ApproveAsync(candidate.Id, Guid.CreateVersion7(), token)).Status);
                    }
                    var policy = new PolicyAdministration(quota);
                    Assert.IsNotNull(await policy.UpdateAsync(new(1, 2, 1, 1), "", Guid.CreateVersion7(), (_, _, _, _) => Task.CompletedTask, token));
                    await using (var db = await quota.CreateDbContextAsync(token))
                        await db.Database.ExecuteSqlRawAsync("INSERT INTO [logquota].[Buckets] ([Kind],[SourceId],[Microtokens],[LastRefill],[PolicyVersion]) SELECT 0,'00000000-0000-0000-0000-000000000000',1000000,TODATETIMEOFFSET(DATEADD(hour,1,SYSUTCDATETIME()),'+00:00'),[Version] FROM [logquota].[Policies] WHERE [Name]=N'central'", token);
                    using var agent = PingTestSite.Client(factory);
                    using var app = PingTestSite.Client(factory);
                    app.DefaultRequestHeaders.Authorization = new("Bearer", appSecret);
                    var queue = new DurableBatchQueue(vault, agentId);
                    Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, batch.EnqueuedAt, 1, hostPayload, token));
                    var hostRequest = new AgentBatchClient(agent, TimeProvider.System).SendNextAsync(vault, server, token);
                    var appRequest = SendAsync(app, "/api/v1/otlp/v1/logs", otlpPayload, "application/x-protobuf", token);
                    await Task.WhenAll(hostRequest, appRequest);
                    var hostOutcome = await hostRequest;
                    using var appResponse = await appRequest;
                    firstHostDelivered = hostOutcome.Status == BatchSendResult.Delivered;
                    Assert.IsTrue((firstHostDelivered && appResponse.StatusCode == HttpStatusCode.ServiceUnavailable) ||
                        (hostOutcome.Status == BatchSendResult.Retry && appResponse.StatusCode == HttpStatusCode.OK));
                    if (firstHostDelivered) Assert.IsNotNull(appResponse.Headers.RetryAfter);
                    else Assert.IsNotNull(hostOutcome.RetryAfter);
                    await using var agentDb = await agents.CreateDbContextAsync(token);
                    await using var serviceDb = await services.CreateDbContextAsync(token);
                    Assert.AreEqual(1, await agentDb.HostLogs.CountAsync(token) + await serviceDb.LogRecords.CountAsync(token));
                }

                using (var restarted = PingTestSite.CreateFactory(database.ConnectionString))
                {
                    using var agent = PingTestSite.Client(restarted);
                    using var app = PingTestSite.Client(restarted);
                    app.DefaultRequestHeaders.Authorization = new("Bearer", appSecret);
                    // Preserve the unsent batch; only create another when the original was acknowledged.
                    var nextBatch = batch with { BatchId = Guid.CreateVersion7(), Items = [batch.Items[0] with { MessageId = Guid.CreateVersion7() }] };
                    var reopenedVault = new IdentityVault(statePath);
                    var queue = new DurableBatchQueue(reopenedVault, agentId);
                    if (firstHostDelivered)
                        Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(nextBatch.BatchId, nextBatch.EnqueuedAt, 1, AgentLogBatchCodec.Serialize(nextBatch), token));
                    var pending = await queue.PeekAsync(token);
                    Assert.IsNotNull(pending);
                    Assert.AreEqual(firstHostDelivered ? nextBatch.BatchId : batch.BatchId, pending.BatchId);
                    CollectionAssert.AreEqual(firstHostDelivered ? AgentLogBatchCodec.Serialize(nextBatch) : hostPayload, pending.Payload);
                    var batchClient = new AgentBatchClient(agent, TimeProvider.System);
                    Assert.AreEqual(BatchSendResult.Retry, (await batchClient.SendNextAsync(reopenedVault, server, token)).Status);
                    var afterRejection = await queue.PeekAsync(token);
                    Assert.IsNotNull(afterRejection);
                    Assert.AreEqual(pending.BatchId, afterRejection.BatchId);
                    CollectionAssert.AreEqual(pending.Payload, afterRejection.Payload);
                    using (var denied = await SendAsync(app, "/api/v1/otlp/v1/logs", otlpPayload, "application/x-protobuf", token))
                        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
                    var quota = restarted.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>();
                    var admin = new PolicyAdministration(quota);
                    var current = await admin.GetAsync(token);
                    Assert.IsNotNull(await admin.UpdateAsync(new(1, 2, 1, 2), current.Version, Guid.CreateVersion7(), (_, _, _, _) => Task.CompletedTask, token));
                    // Fix the observation time and available budget so runner speed cannot refill a bucket mid-test.
                    await using (var db = await quota.CreateDbContextAsync(token))
                        await db.Database.ExecuteSqlRawAsync("UPDATE [logquota].[Buckets] SET [Microtokens]=2000000,[LastRefill]=TODATETIMEOFFSET(DATEADD(hour,1,SYSUTCDATETIME()),'+00:00')", token);
                    var hostAccepted = await batchClient.SendNextAsync(reopenedVault, server, token);
                    using var appAccepted = await SendAsync(app, "/api/v1/otlp/v1/logs", otlpPayload, "application/x-protobuf", token);
                    Assert.AreEqual(BatchSendResult.Delivered, hostAccepted.Status);
                    Assert.IsNull(await queue.PeekAsync(token));
                    Assert.AreEqual(HttpStatusCode.OK, appAccepted.StatusCode);
                    Assert.IsNull(ExportLogsServiceResponse.Parser.ParseFrom(await appAccepted.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
                    var agents = restarted.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
                    var services = restarted.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
                    await using var agentDb = await agents.CreateDbContextAsync(token);
                    await using var serviceDb = await services.CreateDbContextAsync(token);
                    await using var quotaDb = await quota.CreateDbContextAsync(token);
                    Assert.AreEqual(3, await agentDb.HostLogs.CountAsync(token) + await serviceDb.LogRecords.CountAsync(token));
                    Assert.AreEqual(0L, await quotaDb.Database.SqlQueryRaw<long>("SELECT [Microtokens] AS [Value] FROM [logquota].[Buckets] WHERE [Kind]=0").SingleAsync(token));
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (Directory.Exists(statePath)) Directory.Delete(statePath, recursive: true);
            }
        }, context.CancellationToken);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, byte[] payload, string contentType, CancellationToken token)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return client.PostAsync(path, content, token);
    }
}
