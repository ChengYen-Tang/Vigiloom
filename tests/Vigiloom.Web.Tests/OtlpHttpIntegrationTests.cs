using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using AngleSharp.Html.Parser;
using Google.Protobuf;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.WebUtilities;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Web.Telemetry;
using Vigiloom.Web.Api;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using MetricRollupMode = Vigiloom.Modules.ServiceObservability.Domain.Telemetry.MetricRollupMode;
using MetricRollupSeries = Vigiloom.Modules.ServiceObservability.Domain.Telemetry.MetricRollupSeries;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class OtlpHttpIntegrationTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task InFlightPrincipal_IsRejectedAfterRotationAtBothIngresses() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            var factoryDb = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await factoryDb.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using var scope = factory.Services.CreateAsyncScope();
            var credentials = scope.ServiceProvider.GetRequiredService<SourceCredentials>();
            var issued = await credentials.IssueAsync("rotating", token);
            var old = await credentials.AuthenticateVersionedAsync(issued.Secret, token);
            Assert.IsNotNull(old);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, old.SourceId.ToString("D")),
                new Claim("vigiloom:source-version", Convert.ToBase64String(old.Version))], "VigiloomServiceSource"));
            var admin = scope.ServiceProvider.GetRequiredService<SourceAdministration>();
            Assert.AreEqual(SourceRotationStatus.Rotated, (await admin.RotateAsync(old.SourceId, old.Version, Guid.CreateVersion7(), token)).Status);
            var metadata = new DefaultHttpContext { User = principal, RequestServices = factory.Services };
            metadata.Request.ContentType = "application/json";
            metadata.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"serviceKey\":\"orders\",\"environment\":\"prod\",\"displayName\":\"Orders\"}"));
            metadata.Response.Body = new MemoryStream();
            var candidates = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
            await (await ServiceObservationEndpoint.ReceiveAsync(metadata, candidates, token)).ExecuteAsync(metadata);
            Assert.AreEqual(StatusCodes.Status401Unauthorized, metadata.Response.StatusCode);
            var resource = new Resource();
            resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "orders" } });
            resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var request = new ExportLogsServiceRequest { ResourceLogs = { new ResourceLogs { Resource = resource, ScopeLogs = { new ScopeLogs { LogRecords = { new LogRecord() } } } } } };
            var otlp = new DefaultHttpContext { User = principal, RequestServices = factory.Services };
            otlp.Request.ContentType = OtlpResponse.ContentType;
            otlp.Request.Body = new MemoryStream(request.ToByteArray());
            otlp.Response.Body = new MemoryStream();
            var exports = scope.ServiceProvider.GetRequiredService<TelemetryExports>();
            var result = await OtlpHttpReceiver.ReceiveAsync(otlp, ExportLogsServiceRequest.Parser, exports.LogsAsync,
                _ => new ExportLogsServiceResponse(), token);
            await result.ExecuteAsync(otlp);
            Assert.AreEqual(StatusCodes.Status401Unauthorized, otlp.Response.StatusCode);
            await using var check = await factoryDb.CreateDbContextAsync(token);
            Assert.AreEqual(0, await check.Candidates.CountAsync(token));
            Assert.AreEqual(0, await check.LogRecords.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task HttpIngress_EnforcesApprovalAndDoesNotAcknowledgeFailedWrites() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            var factoryDb = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await factoryDb.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var quota = await factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token))
                await quota.Database.MigrateAsync(token);
            await using var scope = factory.Services.CreateAsyncScope();
            var credential = await scope.ServiceProvider.GetRequiredService<SourceCredentials>().IssueAsync("test collector", token);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            const string path = "/api/v1/otlp/v1/logs";
            static ByteArrayContent Body(byte[] data)
            {
                var content = new ByteArrayContent(data);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                return content;
            }
            var gate = factory.Services.GetRequiredService<OtlpConcurrency>().Gate;
            for (var i = 0; i < 4; i++) await gate.WaitAsync(token);
            try
            {
                // An unauthenticated request must be limited before credential lookup.
                using var busy = await client.PostAsync(path, Body([]), token);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            }
            finally { gate.Release(4); }
            using (var anonymous = await client.PostAsync(path, Body([]), token))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                Assert.AreEqual("application/x-protobuf", anonymous.Content.Headers.ContentType?.MediaType);
                Assert.IsNotEmpty(Google.Rpc.Status.Parser.ParseFrom(await anonymous.Content.ReadAsByteArrayAsync(token)).Message);
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
            var resource = new Resource();
            resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "new-service" } });
            resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var log = new LogRecord { TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                TraceId = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 16).ToArray()),
                Body = new AnyValue { StringValue = "private payload" } };
            var scopeLogs = new ScopeLogs { LogRecords = { log } };
            var resourceLogs = new ResourceLogs { Resource = resource, ScopeLogs = { scopeLogs } };
            var request = new ExportLogsServiceRequest { ResourceLogs = { resourceLogs } };
            using (var pending = await client.PostAsync(path, Body(request.ToByteArray()), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, pending.StatusCode);
                var reply = ExportLogsServiceResponse.Parser.ParseFrom(await pending.Content.ReadAsByteArrayAsync(token));
                Assert.AreEqual(1L, reply.PartialSuccess.RejectedLogRecords);
            }
            Guid candidateId;
            await using (var verify = await factoryDb.CreateDbContextAsync(token))
            {
                Assert.AreEqual(1, await verify.Candidates.CountAsync(token));
                Assert.AreEqual(0, await verify.LogRecords.CountAsync(token));
                Assert.AreEqual(0, await verify.LogContexts.CountAsync(token));
                candidateId = (await verify.Candidates.SingleAsync(token)).Id;
            }
            var approved = await scope.ServiceProvider.GetRequiredService<ServiceApproval>().ApproveAsync(candidateId, Guid.CreateVersion7(), token);
            Assert.AreEqual(ApprovalStatus.Approved, approved.Status);
            // A new reading after approval is eligible; pre-approval samples are not backfilled.
            log.TimeUnixNano = (ulong)DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds() * 1_000_000;
            using (var accepted = await client.PostAsync(path, Body(request.ToByteArray()), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
                Assert.IsNull(ExportLogsServiceResponse.Parser.ParseFrom(await accepted.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
            }
            var nanos = log.TimeUnixNano;
            var metric = new Metric { Name = "load", Gauge = new Gauge { DataPoints = { new NumberDataPoint { TimeUnixNano = nanos, AsDouble = 1.5 } } } };
            var metricRequest = new ExportMetricsServiceRequest { ResourceMetrics = { new ResourceMetrics
                { Resource = resource, ScopeMetrics = { new ScopeMetrics { Metrics = { metric } } } } } };
            var span = new OpenTelemetry.Proto.Trace.V1.Span { Name = "request", TraceId = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 16).ToArray()),
                SpanId = ByteString.CopyFrom(Enumerable.Repeat((byte)2, 8).ToArray()), StartTimeUnixNano = nanos, EndTimeUnixNano = nanos };
            var traceRequest = new ExportTraceServiceRequest { ResourceSpans = { new ResourceSpans
                { Resource = resource, ScopeSpans = { new ScopeSpans { Spans = { span } } } } } };
            using (var metrics = await client.PostAsync("/api/v1/otlp/v1/metrics", Body(metricRequest.ToByteArray()), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, metrics.StatusCode);
                Assert.IsNull(ExportMetricsServiceResponse.Parser.ParseFrom(await metrics.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
            }
            await using (var db = await factoryDb.CreateDbContextAsync(token))
            {
                var rollupWindow = await db.MetricRollupWindows.SingleAsync(token);
                rollupWindow.ProjectedRevision = rollupWindow.InputRevision;
                rollupWindow.ProjectionVersion = 1;
                rollupWindow.ProjectedAt = DateTimeOffset.UtcNow;
                rollupWindow.IsComplete = true;
                rollupWindow.RawPruned = true;
                db.MetricRollupSeries.Add(new MetricRollupSeries { WindowRowId = rollupWindow.RowId, ServiceId = rollupWindow.ServiceId,
                    IdentityHash = new byte[32], IdentityBytes = [1], Mode = MetricRollupMode.GaugeSnapshot,
                    ResultVersion = 1, InputCount = 1, ResultPayload = [1, 2] });
                await db.SaveChangesAsync(token);
            }
            using (var pruned = await client.PostAsync("/api/v1/otlp/v1/metrics", Body(metricRequest.ToByteArray()), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, pruned.StatusCode);
                var partial = ExportMetricsServiceResponse.Parser.ParseFrom(await pruned.Content.ReadAsByteArrayAsync(token)).PartialSuccess;
                Assert.IsNotNull(partial);
                Assert.AreEqual(1L, partial.RejectedDataPoints);
                Assert.Contains("no metrics in this request were stored", partial.ErrorMessage);
            }
            using (var traces = await client.PostAsync("/api/v1/otlp/v1/traces", Body(traceRequest.ToByteArray()), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, traces.StatusCode);
                Assert.IsNull(ExportTraceServiceResponse.Parser.ParseFrom(await traces.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
            }
            var second = await scope.ServiceProvider.GetRequiredService<SourceCredentials>().IssueAsync("other source", token);
            var discovery = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(second.SourceId, "billing", "prod", "Billing", token));
            var secondCandidate = (await discovery.ListAsync(1, token)).Single(x => x.SourceId == second.SourceId);
            var otherService = await scope.ServiceProvider.GetRequiredService<ServiceApproval>()
                .ApproveAsync(secondCandidate.Id, Guid.CreateVersion7(), token);
            Assert.AreEqual(ApprovalStatus.Approved, otherService.Status);
            resource.Attributes[0].Value.StringValue = "billing";
            using (var spoofed = await client.PostAsync(path, Body(request.ToByteArray()), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, spoofed.StatusCode);
                Assert.AreEqual(1L, ExportLogsServiceResponse.Parser.ParseFrom(await spoofed.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedLogRecords);
            }
            resource.Attributes[0].Value.StringValue = "new-service";
            await using (var verify = await factoryDb.CreateDbContextAsync(token))
            {
                Assert.AreEqual(1, await verify.LogRecords.CountAsync(token));
                Assert.AreEqual(1, await verify.MetricPoints.CountAsync(token));
                Assert.AreEqual(1, await verify.TraceSpans.CountAsync(token));
                await verify.Database.ExecuteSqlRawAsync("CREATE TRIGGER [service].[RejectOtlpTest] ON [service].[LogRecords] INSTEAD OF INSERT AS THROW 51001, 'failed', 1", token);
            }
            using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));
            var history = $"/api/v1/service-telemetry/{approved.ServiceId:D}";
            var window = $"from={from}&to={to}";
            var rollupFrom = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
            var rollupQuery = $"{history}/metrics/windows?from={rollupFrom}&to={to}";
            using (var noAccess = await reader.GetAsync($"{history}/logs?{window}", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, noAccess.StatusCode);
            using (var noAccess = await reader.GetAsync(rollupQuery, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, noAccess.StatusCode);
            using (var machineDenied = await client.GetAsync($"{history}/logs?{window}", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineDenied.StatusCode);
            using (var machineDenied = await client.GetAsync(rollupQuery, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineDenied.StatusCode);
            await LoginAsync(reader, "reader", token);
            string seriesQuery;
            MetricRollupWindowResponse selectedWindow;
            using (var response = await reader.GetAsync(rollupQuery, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl?.NoStore);
                var windows = await response.Content.ReadFromJsonAsync<MetricRollupWindowPage>(cancellationToken: token);
                Assert.IsNotNull(windows);
                selectedWindow = windows.Items.Single();
                Assert.IsTrue(selectedWindow.RawPruned);
                seriesQuery = $"{history}/metrics/windows/series?start={Uri.EscapeDataString(selectedWindow.WindowStart.ToString("O"))}&version={Uri.EscapeDataString(selectedWindow.VersionToken)}";
            }
            using (var response = await reader.GetAsync(seriesQuery, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var series = await response.Content.ReadFromJsonAsync<MetricRollupSeriesPage>(cancellationToken: token);
                Assert.IsNotNull(series);
                Assert.AreEqual(2, series.Items.Single().PayloadBytes);
            }
            var payloadQuery = seriesQuery.Replace("/series?", "/payload?", StringComparison.Ordinal);
            using (var sourceDenied = await client.GetAsync(payloadQuery, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, sourceDenied.StatusCode);
            var payloadGate = factory.Services.GetRequiredService<MetricRollupPayloadGate>().Gate;
            for (var i = 0; i < 4; i++) await payloadGate.WaitAsync(token);
            try
            {
                using var busyPayload = await reader.GetAsync(payloadQuery, token);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busyPayload.StatusCode);
                Assert.AreEqual("1", busyPayload.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            finally { payloadGate.Release(4); }
            using (var payload = await reader.GetAsync(payloadQuery, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, payload.StatusCode);
                Assert.AreEqual("application/x-protobuf", payload.Content.Headers.ContentType?.MediaType);
                Assert.IsTrue(payload.Headers.CacheControl?.NoStore);
                CollectionAssert.AreEqual(new byte[] { 1, 2 }, await payload.Content.ReadAsByteArrayAsync(token));
            }
            using (var invalidPosition = await reader.GetAsync(payloadQuery + "&position=50", token)) Assert.AreEqual(HttpStatusCode.BadRequest, invalidPosition.StatusCode);
            using (var blockedStream = new BlockingPayloadStream())
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var blockedContext = new DefaultHttpContext();
                blockedContext.Response.Body = blockedStream;
                var admission = factory.Services.GetRequiredService<MetricRollupPayloadGate>();
                var operation = TelemetryHistoryEndpoints.SendMetricPayloadAsync(blockedContext, approved.ServiceId ?? throw new InvalidOperationException("Missing service ID."), selectedWindow.WindowStart,
                    selectedWindow.VersionToken, scope.ServiceProvider.GetRequiredService<TelemetryHistory>(), admission, cancel.Token, 1, 0, null);
                try
                {
                    await blockedStream.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                    Assert.AreEqual(3, admission.Gate.CurrentCount);
                    cancel.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
                    Assert.AreEqual(4, admission.Gate.CurrentCount);
                }
                finally { cancel.Cancel(); try { await operation; } catch (OperationCanceledException) { } }
            }
            async Task VerifyDeadline(bool responseStarted)
            {
                var responseFeature = new TestResponseFeature();
                var lifetime = new AbortTrackingFeature();
                using var blockedStream = new BlockingPayloadStream(() => responseFeature.Started = responseStarted);
                using var deadlineSignal = new CancellationTokenSource();
                using var deadlineGate = new MetricRollupPayloadGate(TimeSpan.FromMinutes(2), deadlineSignal.Token);
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
                var blockedContext = new DefaultHttpContext();
                blockedContext.Features.Set<IHttpResponseFeature>(responseFeature);
                blockedContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
                blockedContext.Response.Body = blockedStream;
                var operation = TelemetryHistoryEndpoints.SendMetricPayloadAsync(blockedContext, approved.ServiceId ?? throw new InvalidOperationException("Missing service ID."),
                    selectedWindow.WindowStart, selectedWindow.VersionToken, scope.ServiceProvider.GetRequiredService<TelemetryHistory>(), deadlineGate, cancel.Token, 1, 0, null);
                try
                {
                    await blockedStream.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                    deadlineSignal.Cancel();
                    var timeout = await operation.WaitAsync(TimeSpan.FromSeconds(10), token);
                    if (responseStarted)
                    {
                        Assert.IsTrue(lifetime.Aborted);
                        Assert.IsFalse(timeout is IStatusCodeHttpResult);
                    }
                    else
                    {
                        var status = Assert.IsInstanceOfType<IStatusCodeHttpResult>(timeout);
                        Assert.AreEqual(StatusCodes.Status504GatewayTimeout, status.StatusCode);
                        Assert.IsNull(blockedContext.Response.ContentLength);
                        Assert.IsNull(blockedContext.Response.ContentType);
                    }
                    Assert.AreEqual(4, deadlineGate.Gate.CurrentCount);
                }
                finally { cancel.Cancel(); try { await operation; } catch (OperationCanceledException) { } }
            }
            await Task.WhenAll(VerifyDeadline(false), VerifyDeadline(true));
            using (var deniedOverview = await reader.GetAsync("/api/v1/service-reception", token)) Assert.AreEqual(HttpStatusCode.Forbidden, deniedOverview.StatusCode);
            using (var deniedPage = await reader.GetAsync("/settings/service-reception", token)) Assert.AreNotEqual(HttpStatusCode.OK, deniedPage.StatusCode);
            using (var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false }))
            {
                await LoginAsync(admin, "admin", token);
                var reception = await admin.GetFromJsonAsync<SourceReceptionSummary[]>("/api/v1/service-reception", token);
                Assert.IsNotNull(reception);
                var active = reception.Single(x => x.SourceId == credential.SourceId);
                Assert.IsNotNull(active.LastLogAt); Assert.IsNotNull(active.LastMetricAt); Assert.IsNotNull(active.LastTraceAt);
                var empty = reception.Single(x => x.SourceId == second.SourceId);
                Assert.IsNull(empty.LastLogAt); Assert.IsNull(empty.LastMetricAt); Assert.IsNull(empty.LastTraceAt);
                using var pageResponse = await admin.GetAsync("/settings/service-reception", token);
                Assert.AreEqual(HttpStatusCode.OK, pageResponse.StatusCode);
                Assert.IsTrue(pageResponse.Headers.CacheControl?.NoStore);
                var html = await pageResponse.Content.ReadAsStringAsync(token);
                var document = await new HtmlParser().ParseDocumentAsync(html, token);
                Assert.Contains("Collector 健康狀態：未知", document.Body!.TextContent);
                Assert.Contains("尚無資料", document.Body.TextContent);
                Assert.Contains("other source", document.Body.TextContent);
                Assert.DoesNotContain(credential.Secret, html);
                Assert.DoesNotContain(second.Secret, html);
                using var emptyPage = await admin.GetAsync("/settings/service-reception?page=2", token);
                var emptyHtml = await emptyPage.Content.ReadAsStringAsync(token);
                Assert.Contains("此頁沒有來源", emptyHtml);
                Assert.Contains("上一頁", emptyHtml);
                Assert.DoesNotContain("目前沒有來源", emptyHtml);
            }
            string apiSecret;
            await using (var identity = await factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>().CreateDbContextAsync(token))
            {
                var actor = await identity.Users.SingleAsync(x => x.UserName == "admin", token);
                var bytes = RandomNumberGenerator.GetBytes(32);
                var key = new ApiCredential("telemetry reader", SHA256.HashData(bytes), false, DateTimeOffset.UtcNow.AddDays(1), actor.Id);
                identity.ApiCredentials.Add(key);
                await identity.SaveChangesAsync(token);
                apiSecret = $"vlg_{key.Id:D}.{WebEncoders.Base64UrlEncode(bytes)}";
                CryptographicOperations.ZeroMemory(bytes);
            }
            using (var api = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false }))
            {
                api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiSecret);
                using var response = await api.GetAsync($"{history}/logs?{window}", token);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl?.NoStore);
                using var windowResponse = await api.GetAsync(rollupQuery, token);
                Assert.AreEqual(HttpStatusCode.OK, windowResponse.StatusCode);
                using var seriesResponse = await api.GetAsync(seriesQuery, token);
                Assert.AreEqual(HttpStatusCode.OK, seriesResponse.StatusCode);
                using var payloadResponse = await api.GetAsync(payloadQuery, token);
                Assert.AreEqual(HttpStatusCode.OK, payloadResponse.StatusCode);
            }
            await using (var db = await factoryDb.CreateDbContextAsync(token))
            {
                var changed = await db.MetricRollupWindows.SingleAsync(token);
                changed.InputRevision++;
                await db.SaveChangesAsync(token);
            }
            using (var changed = await reader.GetAsync(seriesQuery, token))
            {
                Assert.AreEqual(HttpStatusCode.Conflict, changed.StatusCode);
                Assert.Contains("window_changed", await changed.Content.ReadAsStringAsync(token));
            }
            using (var changed = await reader.GetAsync(payloadQuery, token)) Assert.AreEqual(HttpStatusCode.Conflict, changed.StatusCode);
            await using (var db = await factoryDb.CreateDbContextAsync(token))
            {
                var future = await db.MetricRollupWindows.SingleAsync(token);
                future.ProjectionVersion = 2;
                await db.SaveChangesAsync(token);
            }
            var refreshed = await reader.GetFromJsonAsync<MetricRollupWindowPage>(rollupQuery, token);
            Assert.IsNotNull(refreshed);
            var futureWindow = refreshed.Items.Single();
            var futureQuery = $"{history}/metrics/windows/series?start={Uri.EscapeDataString(futureWindow.WindowStart.ToString("O"))}&version={Uri.EscapeDataString(futureWindow.VersionToken)}";
            using (var unsupported = await reader.GetAsync(futureQuery, token))
            {
                Assert.AreEqual(HttpStatusCode.Conflict, unsupported.StatusCode);
                Assert.Contains("unsupported_version", await unsupported.Content.ReadAsStringAsync(token));
            }
            var logPage = await reader.GetFromJsonAsync<TelemetryPage<TelemetryLog, TelemetryContext>>($"{history}/logs?{window}&traceId={Convert.ToHexString(log.TraceId.ToByteArray())}", token);
            Assert.IsNotNull(logPage);
            Assert.HasCount(1, logPage.Items);
            Assert.AreEqual("private payload", LogRecord.Parser.ParseFrom(logPage.Items[0].Record).Body.StringValue);
            Assert.HasCount(1, logPage.Contexts);
            var tracePage = await reader.GetFromJsonAsync<TelemetryPage<TelemetrySpan, TelemetryContext>>($"{history}/traces?{window}", token);
            Assert.IsNotNull(tracePage); Assert.HasCount(1, tracePage.Items);
            var metricPage = await reader.GetFromJsonAsync<TelemetryPage<TelemetryMetricPoint, MetricTelemetryContext>>($"{history}/metrics?{window}", token);
            Assert.IsNotNull(metricPage); Assert.HasCount(1, metricPage.Items);
            var otherPage = await reader.GetFromJsonAsync<TelemetryPage<TelemetryLog, TelemetryContext>>($"/api/v1/service-telemetry/{otherService.ServiceId:D}/logs?{window}", token);
            Assert.IsNotNull(otherPage); Assert.IsEmpty(otherPage.Items);
            using (var invalid = await reader.GetAsync($"{history}/logs?{window}&traceId=invalid", token)) Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using (var invalidPage = await reader.GetAsync($"{history}/metrics?{window}&page=101", token)) Assert.AreEqual(HttpStatusCode.BadRequest, invalidPage.StatusCode);
            using (var failure = await client.PostAsync(path, Body(request.ToByteArray()), token))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
                Assert.IsNotEmpty(Google.Rpc.Status.Parser.ParseFrom(await failure.Content.ReadAsByteArrayAsync(token)).Message);
            }
            await using (var verify = await factoryDb.CreateDbContextAsync(token))
            {
                Assert.AreEqual(1, await verify.LogRecords.CountAsync(token));
                await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER [service].[RejectOtlpTest]", token);
                await verify.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [service].[LogRecords] ([ServiceId],[ContextId],[InstanceId],[OccurredAt],[ReceivedAt],[UsedReceivedTime],[UsedObservedTime],[TraceId],[SpanId],[RecordPayload])
                    SELECT TOP (2500) l.[ServiceId],l.[ContextId],l.[InstanceId],l.[OccurredAt],l.[ReceivedAt],l.[UsedReceivedTime],l.[UsedObservedTime],l.[TraceId],l.[SpanId],l.[RecordPayload]
                    FROM [service].[LogRecords] l CROSS JOIN sys.all_objects a CROSS JOIN sys.all_objects b WHERE l.[ServiceId] = {approved.ServiceId}
                    """, token);
                await verify.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [service].[TraceSpans] ([ServiceId],[InstanceId],[ContextId],[StartedAt],[EndedAt],[ReceivedAt],[TraceId],[SpanId],[ParentSpanId],[SpanPayload])
                    SELECT TOP (2500) s.[ServiceId],s.[InstanceId],s.[ContextId],s.[StartedAt],s.[EndedAt],s.[ReceivedAt],s.[TraceId],s.[SpanId],s.[ParentSpanId],s.[SpanPayload]
                    FROM [service].[TraceSpans] s CROSS JOIN sys.all_objects a CROSS JOIN sys.all_objects b WHERE s.[ServiceId] = {approved.ServiceId}
                    """, token);
                await verify.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [service].[MetricPoints] ([ServiceId],[InstanceId],[ContextId],[OccurredAt],[ReceivedAt],[Kind],[PointPayload])
                    SELECT TOP (2500) m.[ServiceId],m.[InstanceId],m.[ContextId],m.[OccurredAt],m.[ReceivedAt],m.[Kind],m.[PointPayload]
                    FROM [service].[MetricPoints] m CROSS JOIN sys.all_objects a CROSS JOIN sys.all_objects b WHERE m.[ServiceId] = {approved.ServiceId}
                    """, token);
            }
            var lastLogs = await reader.GetFromJsonAsync<TelemetryPage<TelemetryLog, TelemetryContext>>($"{history}/logs?{window}&page=100", token);
            var lastTraces = await reader.GetFromJsonAsync<TelemetryPage<TelemetrySpan, TelemetryContext>>($"{history}/traces?{window}&page=100", token);
            var lastMetrics = await reader.GetFromJsonAsync<TelemetryPage<TelemetryMetricPoint, MetricTelemetryContext>>($"{history}/metrics?{window}&page=100", token);
            Assert.IsNotNull(lastLogs); Assert.IsTrue(lastLogs.Truncated); Assert.IsFalse(lastLogs.HasMore); Assert.HasCount(25, lastLogs.Items);
            Assert.IsNotNull(lastTraces); Assert.IsTrue(lastTraces.Truncated); Assert.IsFalse(lastTraces.HasMore); Assert.HasCount(25, lastTraces.Items);
            Assert.IsNotNull(lastMetrics); Assert.IsTrue(lastMetrics.Truncated); Assert.IsFalse(lastMetrics.HasMore); Assert.HasCount(25, lastMetrics.Items);
            await using (var verify = await factoryDb.CreateDbContextAsync(token))
            {
                await verify.Database.ExecuteSqlInterpolatedAsync($"DELETE TOP (1) FROM [service].[LogRecords] WHERE [ServiceId] = {approved.ServiceId}", token);
                await verify.Database.ExecuteSqlInterpolatedAsync($"DELETE TOP (1) FROM [service].[TraceSpans] WHERE [ServiceId] = {approved.ServiceId}", token);
                await verify.Database.ExecuteSqlInterpolatedAsync($"DELETE TOP (1) FROM [service].[MetricPoints] WHERE [ServiceId] = {approved.ServiceId}", token);
            }
            lastLogs = await reader.GetFromJsonAsync<TelemetryPage<TelemetryLog, TelemetryContext>>($"{history}/logs?{window}&page=100", token);
            lastTraces = await reader.GetFromJsonAsync<TelemetryPage<TelemetrySpan, TelemetryContext>>($"{history}/traces?{window}&page=100", token);
            lastMetrics = await reader.GetFromJsonAsync<TelemetryPage<TelemetryMetricPoint, MetricTelemetryContext>>($"{history}/metrics?{window}&page=100", token);
            Assert.IsNotNull(lastLogs); Assert.IsFalse(lastLogs.Truncated); Assert.IsFalse(lastLogs.HasMore);
            Assert.IsNotNull(lastTraces); Assert.IsFalse(lastTraces.Truncated); Assert.IsFalse(lastTraces.HasMore);
            Assert.IsNotNull(lastMetrics); Assert.IsFalse(lastMetrics.Truncated); Assert.IsFalse(lastMetrics.HasMore);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task MetricRollupPayload_RoundTripsFromOtlpThroughSqlAndHttp() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            var factoryDb = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await factoryDb.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using var scope = factory.Services.CreateAsyncScope();
            var credential = await scope.ServiceProvider.GetRequiredService<SourceCredentials>().IssueAsync("rollup source", token);
            var discovery = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(credential.SourceId, "orders", "prod", "Orders", token));
            var candidate = (await discovery.ListAsync(1, token)).Single(x => x.SourceId == credential.SourceId);
            var approved = await scope.ServiceProvider.GetRequiredService<ServiceApproval>().ApproveAsync(candidate.Id, Guid.CreateVersion7(), token);
            var serviceId = approved.ServiceId ?? throw new InvalidOperationException("Missing service ID.");
            var now = DateTimeOffset.UtcNow;
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300).AddMinutes(-10);
            await using (var db = await factoryDb.CreateDbContextAsync(token))
            {
                var approvedAt = start.AddMinutes(-1);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Services] SET [ApprovedAt] = {approvedAt} WHERE [Id] = {serviceId}", token);
            }
            var timestamp = (ulong)start.AddSeconds(10).ToUnixTimeMilliseconds() * 1_000_000;
            var resource = new Resource();
            resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "orders" } });
            resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var metric = new Metric { Name = "load", Gauge = new Gauge { DataPoints = { new NumberDataPoint { TimeUnixNano = timestamp, AsDouble = 3.5 } } } };
            var request = new ExportMetricsServiceRequest { ResourceMetrics = { new ResourceMetrics { Resource = resource, SchemaUrl = "resource-v1",
                ScopeMetrics = { new ScopeMetrics { Scope = new InstrumentationScope { Name = "meter" }, SchemaUrl = "scope-v1", Metrics = { metric } } } } } };
            using var producer = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            producer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
            using (var body = new ByteArrayContent(request.ToByteArray()))
            {
                body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var accepted = await producer.PostAsync("/api/v1/otlp/v1/metrics", body, token);
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
                Assert.IsNull(ExportMetricsServiceResponse.Parser.ParseFrom(await accepted.Content.ReadAsByteArrayAsync(token)).PartialSuccess);
            }
            Assert.AreEqual(MetricRollupStatus.Complete, await new MetricRollupMaterializer(factoryDb).MaterializeAsync(serviceId, start, token));
            using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            await LoginAsync(reader, "reader", token);
            var prefix = $"/api/v1/service-telemetry/{serviceId:D}/metrics/windows";
            var from = Uri.EscapeDataString(start.ToString("O"));
            var to = Uri.EscapeDataString(start.AddMinutes(5).ToString("O"));
            var windows = await reader.GetFromJsonAsync<MetricRollupWindowPage>($"{prefix}?from={from}&to={to}", token);
            Assert.IsNotNull(windows);
            var window = windows.Items.Single();
            Assert.AreEqual("Current", window.Status);
            var query = $"start={from}&version={Uri.EscapeDataString(window.VersionToken)}";
            var series = await reader.GetFromJsonAsync<MetricRollupSeriesPage>($"{prefix}/series?{query}", token);
            Assert.IsNotNull(series);
            Assert.AreEqual("GaugeSnapshot", series.Items.Single().Mode);
            using var response = await reader.GetAsync($"{prefix}/payload?{query}", token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var decoded = ResourceMetrics.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(token));
            Assert.AreEqual("resource-v1", decoded.SchemaUrl);
            var decodedScope = decoded.ScopeMetrics.Single();
            Assert.AreEqual("scope-v1", decodedScope.SchemaUrl);
            Assert.AreEqual("meter", decodedScope.Scope.Name);
            var saved = decodedScope.Metrics.Single();
            Assert.AreEqual("load", saved.Name);
            Assert.AreEqual(timestamp, saved.Gauge.DataPoints.Single().TimeUnixNano);
            Assert.AreEqual(3.5, saved.Gauge.DataPoints.Single().AsDouble);
        }, context.CancellationToken);

    private static async Task LoginAsync(HttpClient client, string username, CancellationToken token)
    {
        using var page = await client.GetAsync("/account/login", token);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
        var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
        fields["Input.UserName"] = username;
        fields["Input.Password"] = "Strong-Password-123";
        using var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    private sealed class BlockingPayloadStream(Action? onWrite = null) : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onWrite?.Invoke();
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class TestResponseFeature : HttpResponseFeature
    {
        internal bool Started { get; set; }
        public override bool HasStarted => Started;
    }

    private sealed class AbortTrackingFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        internal bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }
}
