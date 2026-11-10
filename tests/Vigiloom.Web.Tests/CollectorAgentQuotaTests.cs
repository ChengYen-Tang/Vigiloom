using System.Net;
using System.Net.Security;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Proto.Logs.V1;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.Tests;

public sealed partial class CollectorInteropTests
{
    private static async Task RunAgentAndCollectorQuotaAsync(WebApplicationFactory<Program> factory, Uri server,
        X509Certificate2 certificate, string parent, string collector, Func<Task<HttpResponseMessage>> sendApplication, CancellationToken token)
    {
        var statePath = Path.Combine(parent, "agent-state");
        var logPath = Path.Combine(parent, "host-quota.log");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var capture = new AgentQuotaCapture(certificate);
        using var agentHttp = new HttpClient(capture);
        IHost? host = null;
        Exception? failure = null;
        try
        {
            await File.WriteAllTextAsync(logPath, "shared-quota-agent\n", token);
            var agentDb = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
            await using (var db = await agentDb.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var device = new Device("collector-quota-host", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            var vault = new IdentityVault(statePath);
            Guid id;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var enrollment = await scope.ServiceProvider.GetRequiredService<AgentIdentity>().IssueAsync(device.Id, Guid.CreateVersion7(), token);
                id = await new IdentityClient(agentHttp).EnrollAsync(vault, server, enrollment.Secret, token);
                var source = await scope.ServiceProvider.GetRequiredService<HostLogConfiguration>().CreateAsync(device.Id,
                    new(AgentLogSourceKind.TextFile, "shared-quota", logPath, true, AgentTextLogMode.SingleLine), Guid.CreateVersion7(), token);
                Assert.AreEqual(HostLogConfigurationStatus.Success, source.Status);
            }
            var quotaFactory = factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>();
            var policyAdmin = new PolicyAdministration(quotaFactory);
            var original = await policyAdmin.GetAsync(token);
            var restricted = await policyAdmin.UpdateAsync(new(1, 10, 1, 10), original.Version, Guid.CreateVersion7(), (_, _, _, _) => Task.CompletedTask, token);
            Assert.IsNotNull(restricted);
            await using (var quota = await quotaFactory.CreateDbContextAsync(token))
                Assert.IsGreaterThan(0, await quota.Database.ExecuteSqlRawAsync("UPDATE [logquota].[Buckets] SET [Microtokens]=0,[LastRefill]=TODATETIMEOFFSET(DATEADD(second,60,SYSUTCDATETIME()),'+00:00') WHERE [Kind]=0", token));

            IHost BuildAgent()
            {
                var builder = AgentHost.CreateBuilder([]);
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Vigiloom:Agent:StateDirectory"] = statePath,
                    ["Vigiloom:Agent:Server"] = server.AbsoluteUri
                });
                // Metrics are covered independently; keep this queue dedicated to the log under test.
                var metricWorker = builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "MetricWorker");
                builder.Services.Remove(metricWorker);
                builder.Services.AddSingleton<TimeProvider>(clock);
                builder.Services.AddSingleton(new AgentSettingsClient(agentHttp, clock));
                builder.Services.AddSingleton(new AgentQueueCapacityClient(agentHttp, clock));
                builder.Services.AddSingleton(new AgentMetricSettingsClient(agentHttp, clock));
                builder.Services.AddSingleton(new AgentLogSettingsClient(agentHttp, clock));
                builder.Services.AddSingleton(new AgentHeartbeatClient(agentHttp));
                builder.Services.AddSingleton(new AgentBatchClient(agentHttp, clock));
                return builder.Build();
            }
            var previousRetries = CountRetries(await DockerAsync(token, "logs", collector), "logs");
            host = BuildAgent();
            await host.StartAsync(token);
            using (var queued = await sendApplication()) Assert.AreEqual(HttpStatusCode.OK, queued.StatusCode);
            var collectorRetry = WaitForRetryAsync(collector, previousRetries, token, "logs");
            for (var attempt = 0; attempt < 120 && !capture.RateLimited.Task.IsCompleted; attempt++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(250, token);
            }
            Assert.IsTrue(capture.RateLimited.Task.IsCompletedSuccessfully, "The real Agent did not observe central quota rejection.");
            await collectorRetry;
            await StopAgentAsync(host);
            host = null;
            var queue = new DurableBatchQueue(vault, id);
            var pending = await queue.PeekAsync(token);
            Assert.IsNotNull(pending);
            Assert.AreEqual(await capture.RateLimited.Task, pending.BatchId);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(pending.Payload, out var logBatch));
            Assert.AreEqual("shared-quota-agent\n", logBatch!.Items.Single().Content);

            var updated = await policyAdmin.UpdateAsync(original.Values, restricted.Version, Guid.CreateVersion7(), (_, _, _, _) => Task.CompletedTask, token);
            Assert.IsNotNull(updated);
            await using (var quota = await quotaFactory.CreateDbContextAsync(token))
                await quota.Database.ExecuteSqlRawAsync("UPDATE [logquota].[Buckets] SET [Microtokens]=100000000,[LastRefill]=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')", token);
            var reopened = await new DurableBatchQueue(new IdentityVault(statePath), id).PeekAsync(token);
            Assert.IsNotNull(reopened);
            Assert.AreEqual(pending.BatchId, reopened.BatchId);
            CollectionAssert.AreEqual(pending.Payload, reopened.Payload);
            host = BuildAgent();
            await host.StartAsync(token);
            var services = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            var delivered = false;
            for (var attempt = 0; attempt < 480; attempt++)
            {
                if (attempt < 100) clock.Advance(TimeSpan.FromSeconds(1));
                await using var hostRecords = await agentDb.CreateDbContextAsync(token);
                await using var appRecords = await services.CreateDbContextAsync(token);
                var hostSaved = await hostRecords.HostLogs.CountAsync(x => x.AgentId == id && x.Content == "shared-quota-agent\n", token);
                var payloads = await appRecords.LogRecords.Select(x => x.RecordPayload).ToArrayAsync(token);
                var appSaved = payloads.Count(x => LogRecord.Parser.ParseFrom(x).Body?.StringValue == "shared-quota-collector");
                if (hostSaved == 1 && appSaved == 1) { delivered = true; break; }
                await Task.Delay(250, token);
            }
            Assert.IsTrue(delivered, "The Agent and Collector did not both resume after the shared policy was restored.");
            var acknowledged = false;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(500));
                try
                {
                    var status = await queue.GetStatusAsync(token);
                    if (status.TotalBatches == 0 && status.QuarantinedBatches == 0) { acknowledged = true; break; }
                }
                catch (IOException) { /* The active Agent may briefly hold its identity lease. */ }
                await Task.Delay(250, token);
            }
            Assert.IsTrue(acknowledged, "The Agent did not acknowledge its stored log batch.");
            await StopAgentAsync(host);
            host = null;
            var finalQueue = new DurableBatchQueue(vault, id);
            var remaining = await finalQueue.PeekAsync(token);
            Assert.IsTrue(remaining is null || remaining.BatchId != pending.BatchId);
            var queueStatus = await finalQueue.GetStatusAsync(token);
            Assert.AreEqual(0L, queueStatus.TotalBatches);
            Assert.AreEqual(0L, queueStatus.QuarantinedBatches);
            await using (var db = await agentDb.CreateDbContextAsync(token))
                Assert.AreEqual(1, await db.ReceivedBatches.CountAsync(x => x.AgentId == id && x.BatchId == pending.BatchId, token));
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            try
            {
                if (host is not null) { await StopAgentAsync(host); host = null; }
                if (File.Exists(logPath)) File.Delete(logPath);
                if (Directory.Exists(statePath)) Directory.Delete(statePath, recursive: true);
            }
            catch (Exception cleanup)
            {
                throw failure is null ? cleanup : new AggregateException("Agent quota test and cleanup failed.", failure, cleanup);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task StopAgentAsync(IHost host)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StopAsync(deadline.Token).WaitAsync(deadline.Token);
        var workers = host.Services.GetServices<IHostedService>().OfType<BackgroundService>().Select(x => x.ExecuteTask).Where(x => x is not null).ToArray();
        if (workers.Any(x => !x!.IsCompleted))
            throw new InvalidOperationException("Agent workers did not stop; state was retained.");
        foreach (var worker in workers)
            if (worker!.IsFaulted) await worker;
        host.Dispose();
    }

    private sealed class AgentQuotaCapture : DelegatingHandler
    {
        public TaskCompletionSource<Guid> RateLimited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentQuotaCapture(X509Certificate2 certificate) : base(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, peer, _, errors) =>
                errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors && peer is not null && peer.GetCertHashString() == certificate.GetCertHashString()
        }) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Guid? batchId = null;
            if (request.RequestUri?.AbsolutePath == "/api/v1/agent/batches" && request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    await using var compressed = new MemoryStream(bytes);
                    await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                    await using var decoded = new MemoryStream();
                    await gzip.CopyToAsync(decoded, cancellationToken);
                    bytes = decoded.ToArray();
                }
                if (AgentLogBatchCodec.TryRead(bytes, out var batch)) batchId = batch!.BatchId;
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (batchId is { } id && response.StatusCode == HttpStatusCode.TooManyRequests &&
                response.Headers.Contains(AgentMetricBatchCodec.SupportedSchemaHeader))
                RateLimited.TrySetResult(id);
            return response;
        }
    }
}
