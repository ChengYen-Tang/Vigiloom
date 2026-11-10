using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.ExceptionServices;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Docker")]
public sealed partial class CollectorInteropTests(TestContext context)
{
    [TestMethod]
    [Timeout(360000, CooperativeCancellation = true)]
    public async Task Collector_SeparatesAuthenticatedSourcesAndForwardsToRealPlatform()
    {
        if (Environment.GetEnvironmentVariable("VIGILOOM_COLLECTOR_INTEGRATION") != "1")
            Assert.Inconclusive("Set VIGILOOM_COLLECTOR_INTEGRATION=1 to run the Docker Collector integration test.");
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), RunAsync, context.CancellationToken);
    }

    private static async Task RunAsync(SqlTestDatabase database, CancellationToken token)
    {
        var name = "vigiloom-collector-test-" + Guid.CreateVersion7().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(directory);
        Exception? failure = null;
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Vigiloom Collector Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost"); names.AddDnsName("host.docker.internal");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            var pfx = generated.Export(X509ContentType.Pfx);
            using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null);
            CryptographicOperations.ZeroMemory(pfx);
            await File.WriteAllTextAsync(Path.Combine(directory, "collector.crt"), certificate.ExportCertificatePem(), token);
            await File.WriteAllTextAsync(Path.Combine(directory, "collector.key"), key.ExportPkcs8PrivateKeyPem(), token);
            await File.WriteAllTextAsync(Path.Combine(directory, "platform-ca.crt"), certificate.ExportCertificatePem(), token);

            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            factory.UseKestrel(options => options.ListenAnyIP(0, listen => listen.UseHttps(certificate)));
            factory.StartServer();
            await PingTestSite.InitializeAsync(factory, token);
            var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var quota = await factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token))
                await quota.Database.MigrateAsync(token);
            await using var scope = factory.Services.CreateAsyncScope();
            var credentials = scope.ServiceProvider.GetRequiredService<SourceCredentials>();
            var sourceA = await credentials.IssueAsync("collector source A", token);
            var sourceB = await credentials.IssueAsync("collector source B", token);
            var monitor = await credentials.IssueAsync("collector self-monitor", token);
            var discovery = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(sourceA.SourceId, "orders", "prod", "Orders", token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(sourceB.SourceId, "billing", "prod", "Billing", token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(monitor.SourceId, "vigiloom-collector", "monitoring", "Collector", token));
            foreach (var candidate in await discovery.ListAsync(1, token))
                Assert.AreEqual(ApprovalStatus.Approved, (await scope.ServiceProvider.GetRequiredService<ServiceApproval>()
                    .ApproveAsync(candidate.Id, Guid.CreateVersion7(), token)).Status);
            var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var port = new Uri(address).Port;
            var ingressA = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var ingressB = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var config = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/collector/isolated-sources.example.yaml"));
            Assert.IsTrue(File.Exists(config), $"Collector config missing: {config}");
            async Task StartCollector(string sourceBToken) { await DockerAsync(token, "run", "-d", "--rm", "--name", name,
                "--mount", $"type=bind,src={directory},dst=/etc/vigiloom/tls,readonly",
                "--mount", $"type=bind,src={config},dst=/etc/collector.yaml,readonly",
                "-p", "127.0.0.1::4318", "-p", "127.0.0.1::4328", "-p", "127.0.0.1::4327",
                "-e", $"VIGILOOM_INGRESS_A_TOKEN={ingressA}", "-e", $"VIGILOOM_INGRESS_B_TOKEN={ingressB}",
                "-e", $"VIGILOOM_SOURCE_A_TOKEN={sourceA.Secret}", "-e", $"VIGILOOM_SOURCE_B_TOKEN={sourceBToken}",
                "-e", $"VIGILOOM_COLLECTOR_MONITOR_TOKEN={monitor.Secret}",
                "-e", $"VIGILOOM_COLLECTOR_METRICS_ENDPOINT=https://host.docker.internal:{port}/api/v1/otlp/v1/metrics",
                "-e", $"VIGILOOM_OTLP_ENDPOINT=https://host.docker.internal:{port}/api/v1/otlp",
                "otel/opentelemetry-collector-contrib:0.164.0", "--config=/etc/collector.yaml"); }
            await StartCollector(sourceB.Secret);
            var aPort = ParsePort(await DockerAsync(token, "port", name, "4318/tcp"));
            var bPort = ParsePort(await DockerAsync(token, "port", name, "4328/tcp"));
            var bGrpcPort = ParsePort(await DockerAsync(token, "port", name, "4327/tcp"));
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors && cert is not null && cert.GetCertHashString() == certificate.GetCertHashString() };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            async Task<HttpResponseMessage> Send(int targetPort, string bearer, string service, string environment = "prod", int count = 1, string? bodyText = null)
            {
                var resource = new Resource { Attributes = {
                    new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = service } },
                    new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = environment } } } };
                var logs = new ScopeLogs();
                for (var i = 0; i < count; i++) logs.LogRecords.Add(new LogRecord { Body = new AnyValue { StringValue = bodyText ?? (count == 1 ? "collector-test" : $"quota-test-{i}") } });
                var payload = new ExportLogsServiceRequest { ResourceLogs = { new ResourceLogs { Resource = resource, ScopeLogs = { logs } } } };
                var body = new ByteArrayContent(payload.ToByteArray());
                body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                var message = new HttpRequestMessage(HttpMethod.Post, $"https://localhost:{targetPort}/v1/logs") { Content = body };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                return await http.SendAsync(message, token);
            }
            // The container can take a moment to bind its TLS endpoints.
            await WaitForCollectorAsync(http, aPort, token);
            using (var untrusted = new HttpClient())
                await Assert.ThrowsAsync<HttpRequestException>(() => untrusted.GetAsync($"https://localhost:{aPort}/", token));
            using (var forbidden = await Send(bPort, ingressA, "billing")) Assert.AreEqual(HttpStatusCode.Unauthorized, forbidden.StatusCode);
            using (var spoof = await Send(aPort, ingressA, "billing")) Assert.AreEqual(HttpStatusCode.OK, spoof.StatusCode);
            using (var spoofMonitor = await Send(aPort, ingressA, "vigiloom-collector", "monitoring")) Assert.AreEqual(HttpStatusCode.OK, spoofMonitor.StatusCode);
            await WaitForCandidateAsync(dbFactory, sourceA.SourceId, "vigiloom-collector", token);
            // Wait until the attempted spoof has reached the platform, not just the Collector's memory buffer.
            await WaitForCandidateAsync(dbFactory, sourceA.SourceId, "billing", token);
            // Discovery commits before the formal export transaction. The Collector's
            // partial-success warning proves the receiver completed its rejection.
            await WaitForCollectorLogAsync(name, "Some data was invalid or not authorized for collection.", token);
            using (var accepted = await Send(bPort, ingressB, "billing")) Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
            try { await WaitForRecordsAsync(dbFactory, 1, token); }
            catch
            {
                var logs = await DockerAsync(token, "logs", name);
                logs = logs.Replace(sourceA.Secret, "[redacted]", StringComparison.Ordinal)
                    .Replace(sourceB.Secret, "[redacted]", StringComparison.Ordinal)
                    .Replace(monitor.Secret, "[redacted]", StringComparison.Ordinal)
                    .Replace(ingressA, "[redacted]", StringComparison.Ordinal)
                    .Replace(ingressB, "[redacted]", StringComparison.Ordinal);
                throw new InvalidOperationException($"Collector did not deliver records. Diagnostics: {logs}");
            }
            await using var verify = await dbFactory.CreateDbContextAsync(token);
            var billing = await verify.Services.SingleAsync(x => x.SourceId == sourceB.SourceId && x.ServiceKey == "billing", token);
            Assert.AreEqual(1, await verify.LogRecords.CountAsync(x => x.ServiceId == billing.Id, token));
            Assert.AreEqual(1, await verify.LogRecords.CountAsync(token));
            var previousRetries = CountRetries(await DockerAsync(token, "logs", name), "logs");
            await verify.Database.ExecuteSqlRawAsync("CREATE TRIGGER [service].[CollectorWriteFailure] ON [service].[LogRecords] INSTEAD OF INSERT AS THROW 51001, 'test failure', 1", token);
            using (var queued = await Send(bPort, ingressB, "billing")) Assert.AreEqual(HttpStatusCode.OK, queued.StatusCode);
            await WaitForRetryAsync(name, previousRetries, token, "logs");
            Assert.AreEqual(1, await verify.LogRecords.CountAsync(token));
            await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER [service].[CollectorWriteFailure]", token);
            await WaitForRecordsAsync(dbFactory, 2, token);
            var quotaAdmin = scope.ServiceProvider.GetRequiredService<PolicyAdministration>();
            var originalPolicy = await quotaAdmin.GetAsync(token);
            var restrictedPolicy = await quotaAdmin.UpdateAsync(new LogQuotaValues(1, 10, 1, 10), originalPolicy.Version,
                Guid.CreateVersion7(), (_, _, _, _) => Task.CompletedTask, token);
            Assert.IsNotNull(restrictedPolicy);
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                var exhausted = await Vigiloom.Infrastructure.LogAdmission.LogAdmission.AdmitAsync(connection, transaction,
                    new LogSource(LogSourceKind.Application, sourceB.SourceId), 10, token);
                Assert.AreEqual(LogAdmissionStatus.Accepted, exhausted.Status);
                await transaction.CommitAsync(token);
            }
            await using (var quota = await factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token))
                await quota.Database.ExecuteSqlInterpolatedAsync($"UPDATE [logquota].[Buckets] SET [LastRefill] = TODATETIMEOFFSET(DATEADD(second, 20, SYSUTCDATETIME()), '+00:00') WHERE ([Kind] = 0 AND [SourceId] = {Guid.Empty}) OR ([Kind] = {(byte)LogSourceKind.Application} AND [SourceId] = {sourceB.SourceId})", token);
            var retriesBeforeQuota = CountRetries(await DockerAsync(token, "logs", name), "logs");
            using (var queued = await Send(bPort, ingressB, "billing", count: 9)) Assert.AreEqual(HttpStatusCode.OK, queued.StatusCode);
            await WaitForRetryAsync(name, retriesBeforeQuota, token, "logs");
            Assert.AreEqual(2, await verify.LogRecords.CountAsync(x => x.ServiceId == billing.Id, token));
            var restoredPolicy = await quotaAdmin.UpdateAsync(LogQuotaValues.Default, restrictedPolicy.Version,
                Guid.CreateVersion7(), (_, _, _, _) => Task.CompletedTask, token);
            Assert.IsNotNull(restoredPolicy);
            await using (var quota = await factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token))
                await quota.Database.ExecuteSqlInterpolatedAsync($"UPDATE [logquota].[Buckets] SET [LastRefill] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') WHERE ([Kind] = 0 AND [SourceId] = {Guid.Empty}) OR ([Kind] = {(byte)LogSourceKind.Application} AND [SourceId] = {sourceB.SourceId})", token);
            await WaitForRecordsAsync(dbFactory, 11, token);
            var quotaPayloads = await verify.LogRecords.Where(x => x.ServiceId == billing.Id).Select(x => x.RecordPayload).ToArrayAsync(token);
            Assert.AreEqual(9, quotaPayloads.Select(x => LogRecord.Parser.ParseFrom(x).Body?.StringValue)
                .Count(x => x?.StartsWith("quota-test-", StringComparison.Ordinal) == true));
            var billingResource = new Resource { Attributes = {
                new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "billing" } },
                new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } },
                new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = "collector-test-billing" } } } };
            var nanos = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var metric = new Metric { Name = "collector.integration.metric", Gauge = new Gauge { DataPoints = {
                new NumberDataPoint { TimeUnixNano = nanos, AsDouble = 42.5 } } } };
            var metricRequest = new ExportMetricsServiceRequest { ResourceMetrics = { new ResourceMetrics { Resource = billingResource,
                ScopeMetrics = { new ScopeMetrics { Metrics = { metric } } } } } };
            var traceId = ByteString.CopyFrom(Enumerable.Repeat((byte)0x21, 16).ToArray());
            var spanId = ByteString.CopyFrom(Enumerable.Repeat((byte)0x31, 8).ToArray());
            var span = new Span { Name = "collector.integration.span", TraceId = traceId, SpanId = spanId,
                StartTimeUnixNano = nanos, EndTimeUnixNano = nanos };
            var traceRequest = new ExportTraceServiceRequest { ResourceSpans = { new ResourceSpans { Resource = billingResource,
                ScopeSpans = { new ScopeSpans { Spans = { span } } } } } };
            async Task<HttpResponseMessage> SendSignal(string signal, byte[] payload)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://localhost:{bPort}/v1/{signal}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingressB);
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                return await http.SendAsync(request, token);
            }
            var metricRetries = CountRetries(await DockerAsync(token, "logs", name), "metrics");
            await verify.Database.ExecuteSqlRawAsync("CREATE TRIGGER [service].[CollectorMetricWriteFailure] ON [service].[MetricPoints] INSTEAD OF INSERT AS THROW 51001, 'test failure', 1", token);
            using (var accepted = await SendSignal("metrics", metricRequest.ToByteArray())) Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
            await WaitForRetryAsync(name, metricRetries, token, "metrics");
            var failedMetricName = System.Text.Encoding.Unicode.GetBytes("collector.integration.metric");
            Assert.IsFalse(await verify.MetricPoints.AnyAsync(x => x.ServiceId == billing.Id && x.Context.MetricNameBytes == failedMetricName, token));
            await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER [service].[CollectorMetricWriteFailure]", token);
            using (var accepted = await SendSignal("traces", traceRequest.ToByteArray())) Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
            var delivered = false;
            var metricName = System.Text.Encoding.Unicode.GetBytes("collector.integration.metric");
            var expectedTraceId = traceId.ToByteArray();
            var expectedSpanId = spanId.ToByteArray();
            for (var i = 0; i < 100; i++)
            {
                await using var observations = await dbFactory.CreateDbContextAsync(token);
                var hasMetric = await observations.MetricPoints.AnyAsync(x => x.ServiceId == billing.Id && x.Context.MetricNameBytes == metricName, token);
                var hasTrace = await observations.TraceSpans.AnyAsync(x => x.ServiceId == billing.Id && x.TraceId == expectedTraceId && x.SpanId == expectedSpanId, token);
                if (hasMetric && hasTrace) { delivered = true; break; }
                await Task.Delay(250, token);
            }
            Assert.IsTrue(delivered, "The Collector did not persist both the billing metric and trace.");
            Guid billingInstanceId;
            await using (var persisted = await dbFactory.CreateDbContextAsync(token))
            {
                var storedMetric = await persisted.MetricPoints.SingleAsync(x => x.ServiceId == billing.Id && x.Context.MetricNameBytes == metricName, token);
                var storedSpan = await persisted.TraceSpans.SingleAsync(x => x.ServiceId == billing.Id && x.TraceId == expectedTraceId && x.SpanId == expectedSpanId, token);
                Assert.AreEqual(42.5, NumberDataPoint.Parser.ParseFrom(storedMetric.PointPayload).AsDouble);
                var savedSpan = Span.Parser.ParseFrom(storedSpan.SpanPayload);
                Assert.AreEqual("collector.integration.span", savedSpan.Name);
                Assert.AreEqual(nanos, savedSpan.StartTimeUnixNano);
                Assert.IsNotNull(storedMetric.InstanceId);
                billingInstanceId = storedMetric.InstanceId.Value;
                Assert.AreEqual(storedMetric.InstanceId, storedSpan.InstanceId);
                Assert.AreEqual("collector-test-billing", await persisted.Instances.Where(x => x.Id == storedSpan.InstanceId)
                    .Select(x => x.ExternalInstanceId).SingleAsync(token));
            }
            var traceQuotaAdmin = scope.ServiceProvider.GetRequiredService<TracePolicyAdministration>();
            var originalTracePolicy = await traceQuotaAdmin.GetAsync(token);
            var restrictedTracePolicy = await traceQuotaAdmin.UpdateAsync(new LogQuotaValues(1, 10, 1, 10), originalTracePolicy.Version,
                Guid.CreateVersion7(), (_, _, _, _) => Task.CompletedTask, token);
            Assert.IsNotNull(restrictedTracePolicy);
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                var exhausted = await TraceAdmission.AdmitAsync(connection, transaction, [new Vigiloom.Infrastructure.LogAdmission.TraceSource(sourceB.SourceId, billing.Id, 10)], token);
                Assert.AreEqual(LogAdmissionStatus.Accepted, exhausted.Status);
                await transaction.CommitAsync(token);
            }
            await using (var quota = await factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token))
                await quota.Database.ExecuteSqlInterpolatedAsync($"UPDATE [logquota].[TraceBuckets] SET [LastRefill] = TODATETIMEOFFSET(DATEADD(second, 20, SYSUTCDATETIME()), '+00:00') WHERE ([SourceId] = {Guid.Empty} AND [ServiceId] = {Guid.Empty}) OR ([SourceId] = {sourceB.SourceId} AND [ServiceId] = {billing.Id})", token);
            var quotaTraceId = ByteString.CopyFrom(Enumerable.Repeat((byte)0x41, 16).ToArray());
            var quotaSpans = new ScopeSpans();
            for (var i = 0; i < 9; i++)
                quotaSpans.Spans.Add(new Span { Name = "trace-quota-test", TraceId = quotaTraceId,
                    SpanId = ByteString.CopyFrom(Enumerable.Repeat((byte)(i + 1), 8).ToArray()), StartTimeUnixNano = nanos, EndTimeUnixNano = nanos });
            var traceQuotaRequest = new ExportTraceServiceRequest { ResourceSpans = { new ResourceSpans { Resource = billingResource, ScopeSpans = { quotaSpans } } } };
            var retriesBeforeTraceQuota = CountRetries(await DockerAsync(token, "logs", name), "traces");
            using (var accepted = await SendSignal("traces", traceQuotaRequest.ToByteArray())) Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
            await WaitForRetryAsync(name, retriesBeforeTraceQuota, token, "traces");
            var traceQuotaBytes = quotaTraceId.ToByteArray();
            Assert.AreEqual(0, await verify.TraceSpans.CountAsync(x => x.ServiceId == billing.Id && x.TraceId == traceQuotaBytes, token));
            Assert.IsNotNull(await traceQuotaAdmin.UpdateAsync(LogQuotaValues.TraceDefault, restrictedTracePolicy.Version,
                Guid.CreateVersion7(), (_, _, _, _) => Task.CompletedTask, token));
            await using (var quota = await factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token))
                await quota.Database.ExecuteSqlInterpolatedAsync($"UPDATE [logquota].[TraceBuckets] SET [LastRefill] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') WHERE ([SourceId] = {Guid.Empty} AND [ServiceId] = {Guid.Empty}) OR ([SourceId] = {sourceB.SourceId} AND [ServiceId] = {billing.Id})", token);
            var traceQuotaDelivered = false;
            for (var i = 0; i < 240; i++)
            {
                await using var observations = await dbFactory.CreateDbContextAsync(token);
                if (await observations.TraceSpans.CountAsync(x => x.ServiceId == billing.Id && x.TraceId == traceQuotaBytes, token) == 9)
                { traceQuotaDelivered = true; break; }
                await Task.Delay(250, token);
            }
            Assert.IsTrue(traceQuotaDelivered, "Collector did not resume trace delivery after quota recovery.");
            await using (var observations = await dbFactory.CreateDbContextAsync(token))
            {
                var actualSpans = await observations.TraceSpans.Where(x => x.ServiceId == billing.Id && x.TraceId == traceQuotaBytes)
                    .Select(x => x.SpanId).ToArrayAsync(token);
                CollectionAssert.AreEquivalent(Enumerable.Range(1, 9).Select(i => Convert.ToHexString(Enumerable.Repeat((byte)i, 8).ToArray())).ToArray(),
                    actualSpans.Select(x => Convert.ToHexString(x)).ToArray());
            }
            using var grpcHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors && cert is not null && cert.GetCertHashString() == certificate.GetCertHashString() };
            using var channel = GrpcChannel.ForAddress($"https://localhost:{bGrpcPort}", new GrpcChannelOptions { HttpHandler = grpcHandler });
            var grpcHeaders = new Metadata { { "authorization", $"Bearer {ingressB}" } };
            var grpcLog = new ExportLogsServiceRequest { ResourceLogs = { new ResourceLogs { Resource = billingResource,
                ScopeLogs = { new ScopeLogs { LogRecords = { new LogRecord { Body = new AnyValue { StringValue = "grpc-collector-test" } } } } } } } };
            var grpcMetric = metricRequest.Clone();
            grpcMetric.ResourceMetrics[0].ScopeMetrics[0].Metrics[0].Name = "collector.grpc.metric";
            var grpcTrace = traceRequest.Clone();
            var grpcSpan = grpcTrace.ResourceSpans[0].ScopeSpans[0].Spans[0];
            grpcSpan.TraceId = ByteString.CopyFrom(Enumerable.Repeat((byte)0x22, 16).ToArray());
            grpcSpan.SpanId = ByteString.CopyFrom(Enumerable.Repeat((byte)0x32, 8).ToArray());
            var logsClient = new LogsService.LogsServiceClient(channel);
            var metricsClient = new MetricsService.MetricsServiceClient(channel);
            var tracesClient = new TraceService.TraceServiceClient(channel);
            var wrongHeaders = new Metadata { { "authorization", $"Bearer {ingressA}" } };
            var oversizedGrpc = grpcLog.Clone();
            oversizedGrpc.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Body.StringValue = new string('x', 4 * 1024 * 1024);
            using (var oversized = new HttpRequestMessage(HttpMethod.Post, $"https://localhost:{bPort}/v1/logs"))
            {
                oversized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingressB);
                oversized.Content = new ByteArrayContent(oversizedGrpc.ToByteArray());
                oversized.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var rejected = await http.SendAsync(oversized, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
                Assert.Contains("too large", System.Text.Encoding.UTF8.GetString(await rejected.Content.ReadAsByteArrayAsync(token)), StringComparison.OrdinalIgnoreCase);
            }
            var grpcLimit = await Assert.ThrowsAsync<RpcException>(async () =>
                await logsClient.ExportAsync(oversizedGrpc, grpcHeaders, cancellationToken: token).ResponseAsync);
            Assert.AreEqual(StatusCode.ResourceExhausted, grpcLimit.StatusCode);
            var denied = await Assert.ThrowsAsync<RpcException>(async () =>
                await logsClient.ExportAsync(grpcLog, wrongHeaders, cancellationToken: token).ResponseAsync);
            Assert.AreEqual(StatusCode.Unauthenticated, denied.StatusCode);
            var deniedMetrics = await Assert.ThrowsAsync<RpcException>(async () =>
                await metricsClient.ExportAsync(grpcMetric, wrongHeaders, cancellationToken: token).ResponseAsync);
            var deniedTraces = await Assert.ThrowsAsync<RpcException>(async () =>
                await tracesClient.ExportAsync(grpcTrace, wrongHeaders, cancellationToken: token).ResponseAsync);
            Assert.AreEqual(StatusCode.Unauthenticated, deniedMetrics.StatusCode);
            Assert.AreEqual(StatusCode.Unauthenticated, deniedTraces.StatusCode);
            await logsClient.ExportAsync(grpcLog, grpcHeaders, cancellationToken: token).ResponseAsync;
            await metricsClient.ExportAsync(grpcMetric, grpcHeaders, cancellationToken: token).ResponseAsync;
            await tracesClient.ExportAsync(grpcTrace, grpcHeaders, cancellationToken: token).ResponseAsync;
            var grpcMetricName = System.Text.Encoding.Unicode.GetBytes("collector.grpc.metric");
            var grpcTraceId = grpcSpan.TraceId.ToByteArray();
            var grpcDelivered = false;
            for (var i = 0; i < 100; i++)
            {
                await using var observations = await dbFactory.CreateDbContextAsync(token);
                var logPayloads = await observations.LogRecords.Where(x => x.ServiceId == billing.Id && x.InstanceId == billingInstanceId)
                    .Select(x => x.RecordPayload).Take(20).ToArrayAsync(token);
                var hasLog = logPayloads.Any(x => LogRecord.Parser.ParseFrom(x).Body?.StringValue == "grpc-collector-test");
                var hasMetric = await observations.MetricPoints.AnyAsync(x => x.ServiceId == billing.Id && x.Context.MetricNameBytes == grpcMetricName, token);
                var hasTrace = await observations.TraceSpans.AnyAsync(x => x.ServiceId == billing.Id && x.TraceId == grpcTraceId, token);
                if (hasLog && hasMetric && hasTrace) { grpcDelivered = true; break; }
                await Task.Delay(250, token);
            }
            Assert.IsTrue(grpcDelivered, "The Collector did not persist all three gRPC signals.");
            await using (var persisted = await dbFactory.CreateDbContextAsync(token))
            {
                var savedMetric = await persisted.MetricPoints.SingleAsync(x => x.ServiceId == billing.Id && x.Context.MetricNameBytes == grpcMetricName, token);
                var savedTrace = await persisted.TraceSpans.SingleAsync(x => x.ServiceId == billing.Id && x.TraceId == grpcTraceId, token);
                Assert.AreEqual(42.5, NumberDataPoint.Parser.ParseFrom(savedMetric.PointPayload).AsDouble);
                Assert.AreEqual(billingInstanceId, savedMetric.InstanceId);
                Assert.AreEqual(billingInstanceId, savedTrace.InstanceId);
                CollectionAssert.AreEqual(grpcSpan.SpanId.ToByteArray(), savedTrace.SpanId);
                Assert.AreEqual(nanos, Span.Parser.ParseFrom(savedTrace.SpanPayload).StartTimeUnixNano);
            }
            var authenticated = await credentials.AuthenticateVersionedAsync(sourceB.Secret, token);
            Assert.IsNotNull(authenticated);
            var rotation = await scope.ServiceProvider.GetRequiredService<SourceAdministration>()
                .RotateAsync(sourceB.SourceId, authenticated.Version, Guid.CreateVersion7(), token);
            Assert.AreEqual(SourceRotationStatus.Rotated, rotation.Status);
            Assert.IsNotNull(rotation.Credential);
            Assert.IsNull(await credentials.AuthenticateVersionedAsync(sourceB.Secret, token));
            Assert.IsNotNull(await credentials.AuthenticateVersionedAsync(rotation.Credential, token));
            await DockerAsync(token, "rm", "-f", name);
            await StartCollector(rotation.Credential);
            bPort = ParsePort(await DockerAsync(token, "port", name, "4328/tcp"));
            await WaitForCollectorAsync(http, bPort, token);
            using (var afterRotation = await Send(bPort, ingressB, "billing", bodyText: "rotated-credential-check"))
                Assert.AreEqual(HttpStatusCode.OK, afterRotation.StatusCode);
            var rotationDelivered = false;
            for (var i = 0; i < 100; i++)
            {
                await using var observations = await dbFactory.CreateDbContextAsync(token);
                var payloads = await observations.LogRecords.Where(x => x.ServiceId == billing.Id)
                    .Select(x => x.RecordPayload).ToArrayAsync(token);
                if (payloads.Any(x => LogRecord.Parser.ParseFrom(x).Body?.StringValue == "rotated-credential-check"))
                { rotationDelivered = true; break; }
                await Task.Delay(250, token);
            }
            Assert.IsTrue(rotationDelivered, "Collector did not resume delivery after central credential rotation and restart.");
            await RunAgentAndCollectorQuotaAsync(factory, new Uri($"https://localhost:{port}/"), certificate,
                directory, name, () => Send(bPort, ingressB, "billing", bodyText: "shared-quota-collector"), token);
            for (var i = 0; i < 160; i++)
            {
                await using var metrics = await dbFactory.CreateDbContextAsync(token);
                if (await (from point in metrics.MetricPoints join service in metrics.Services on point.ServiceId equals service.Id
                           where service.SourceId == monitor.SourceId select point.RowId).AnyAsync(token))
                {
                    var payloads = await (from entry in metrics.MetricContexts join service in metrics.Services on entry.ServiceId equals service.Id
                                          where service.SourceId == monitor.SourceId select entry.MetricPayload).ToArrayAsync(token);
                    var metricNames = payloads.Select(x => OpenTelemetry.Proto.Metrics.V1.Metric.Parser.ParseFrom(x).Name).ToArray();
                    if (metricNames.Contains("otelcol_process_uptime", StringComparer.Ordinal) &&
                        metricNames.Contains("otelcol_exporter_sent_log_records", StringComparer.Ordinal))
                    {
                        var uptime = System.Text.Encoding.Unicode.GetBytes("otelcol_process_uptime");
                        Assert.IsTrue(await (from point in metrics.MetricPoints join service in metrics.Services on point.ServiceId equals service.Id
                            where service.SourceId == monitor.SourceId && point.Kind == 2 && point.Context.MetricNameBytes == uptime
                            select point.RowId).AnyAsync(token));
                        var sentName = System.Text.Encoding.Unicode.GetBytes("otelcol_exporter_sent_log_records");
                        var sent = await (from point in metrics.MetricPoints join service in metrics.Services on point.ServiceId equals service.Id
                                          where service.SourceId == monitor.SourceId && point.Kind == 2 && point.Context.MetricNameBytes == sentName
                                          select new { point.PointPayload, point.Context.MetricPayload }).ToArrayAsync(token);
                        if (sent.Any(x =>
                        {
                            var metric = OpenTelemetry.Proto.Metrics.V1.Metric.Parser.ParseFrom(x.MetricPayload);
                            var point = OpenTelemetry.Proto.Metrics.V1.NumberDataPoint.Parser.ParseFrom(x.PointPayload);
                            return metric.Sum is { IsMonotonic: true, AggregationTemporality: OpenTelemetry.Proto.Metrics.V1.AggregationTemporality.Cumulative } &&
                                point.Attributes.Any(a => a.Key == "exporter" && a.Value.StringValue == "otlp_http/source-b");
                        }))
                            return;
                    }
                }
                await Task.Delay(500, token);
            }
            Assert.Fail("Collector uptime and the source-b exporter sent metric were not both persisted before the deadline.");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var result = await DockerAsync(cleanup.Token, "rm", "-f", name, allowFailure: true);
                if (!string.Equals(result.Trim(), name, StringComparison.Ordinal) && !result.Contains("No such container", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Could not confirm cleanup of container {name}: {result}");
            }
            catch (Exception exception) { cleanupFailure = exception; }
            try
            {
                foreach (var file in new[] { "collector.crt", "collector.key", "platform-ca.crt" }) File.Delete(Path.Combine(directory, file));
                Directory.Delete(directory);
            }
            catch (Exception exception) { cleanupFailure = cleanupFailure is null ? exception : new AggregateException(cleanupFailure, exception); }
            if (cleanupFailure is not null)
                throw failure is null ? cleanupFailure : new AggregateException("Collector test and cleanup failed.", failure, cleanupFailure);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task WaitForCollectorAsync(HttpClient client, int port, CancellationToken token)
    {
        for (var i = 0; i < 40; i++)
        {
            try { using var response = await client.GetAsync($"https://localhost:{port}/", token); return; }
            catch (HttpRequestException) { await Task.Delay(250, token); }
        }
        Assert.Fail("Collector did not become reachable.");
    }

    private static async Task WaitForRecordsAsync(IDbContextFactory<ServiceObservabilityDbContext> factory, int count, CancellationToken token)
    {
        for (var i = 0; i < 240; i++)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            if (await db.LogRecords.CountAsync(token) >= count) return;
            await Task.Delay(250, token);
        }
        Assert.Fail("Collector did not deliver the expected records.");
    }

    private static async Task WaitForCandidateAsync(IDbContextFactory<ServiceObservabilityDbContext> factory, Guid sourceId, string service, CancellationToken token)
    {
        for (var i = 0; i < 60; i++)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            if (await db.Candidates.AnyAsync(x => x.SourceId == sourceId && x.ServiceKey == service, token)) return;
            await Task.Delay(250, token);
        }
        Assert.Fail("Collector did not forward the spoof attempt to its own source scope.");
    }

    private static async Task WaitForRetryAsync(string container, int previousRetries, CancellationToken token, string? signal = null)
    {
        for (var i = 0; i < 120; i++)
        {
            var logs = await DockerAsync(token, "logs", container);
            if (CountRetries(logs, signal) > previousRetries) return;
            await Task.Delay(250, token);
        }
        Assert.Fail("Collector did not report retrying a platform 503.");
    }

    private static int CountRetries(string logs, string? signal = null) => logs.Split('\n').Count(line =>
        line.Contains("503", StringComparison.Ordinal) && line.Contains("Will retry", StringComparison.Ordinal) &&
        (signal is null || line.Contains($"\"{signal}\"", StringComparison.Ordinal) && line.Contains("\"otlp_http/source-b\"", StringComparison.Ordinal)));

    private static async Task WaitForCollectorLogAsync(string container, string expected, CancellationToken token)
    {
        for (var i = 0; i < 120; i++)
        {
            var logs = await DockerAsync(token, "logs", container);
            if (logs.Contains(expected, StringComparison.Ordinal)) return;
            await Task.Delay(250, token);
        }
        Assert.Fail("Collector did not report the expected completed export response.");
    }

    private static int ParsePort(string output) => int.Parse(output.Trim().Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> DockerAsync(CancellationToken token, params string[] args) => await DockerAsync(token, args, false);
    private static async Task<string> DockerAsync(CancellationToken token, string a, string b, string c, bool allowFailure) =>
        await DockerAsync(token, [a, b, c], allowFailure);
    private static async Task<string> DockerAsync(CancellationToken token, string[] args, bool allowFailure)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(stop.Token);
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException($"Docker exited with {process.ExitCode}: {error}");
        return output + error;
    }
}
