using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AngleSharp.Html.Parser;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Api;
using Vigiloom.Web.Telemetry;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReceptionRejectionTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ConcurrentNodes_AddDeltasWithoutOverwritingAndMissingRowsAreVisible() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            using var scope = factory.Services.CreateScope();
            var credentials = scope.ServiceProvider.GetRequiredService<SourceCredentials>();
            var firstSource = await credentials.IssueAsync("first", token);
            var secondSource = await credentials.IssueAsync("second", token);
            var first = new ReceptionRejectionDiagnostics(dbFactory, NullLogger<ReceptionRejectionDiagnostics>.Instance);
            var second = new ReceptionRejectionDiagnostics(dbFactory, NullLogger<ReceptionRejectionDiagnostics>.Instance);
            for (var i = 0; i < 1000; i++) first.Record(ReceptionRejectionKind.SourceUnavailable, ReceptionIngress.Otlp, firstSource.SourceId);
            var producer = Task.Run(() => Parallel.For(0, 10000, _ => first.Record(ReceptionRejectionKind.SourceUnavailable, ReceptionIngress.Otlp, firstSource.SourceId)), token);
            for (var i = 0; i < 1000; i++) second.Record(ReceptionRejectionKind.SourceUnavailable, ReceptionIngress.Discovery, secondSource.SourceId);
            await Task.WhenAll(first.FlushAsync(token), second.FlushAsync(token), producer);
            await first.FlushAsync(token);
            var reader = new ReceptionRejectionReader(dbFactory);
            var summary = (await reader.ListAsync(token)).Single(x => x.Kind == 2);
            Assert.AreEqual(12000L, summary.Count);
            Assert.IsTrue((summary.ExampleIngress == (byte)ReceptionIngress.Otlp && summary.ExampleSourceId == firstSource.SourceId) ||
                (summary.ExampleIngress == (byte)ReceptionIngress.Discovery && summary.ExampleSourceId == secondSource.SourceId));
            await using (var db = await dbFactory.CreateDbContextAsync(token))
                await db.Database.ExecuteSqlRawAsync("DELETE FROM [service].[ReceptionRejections] WHERE [Kind] = 2", token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ListAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Rejections_AreBoundedVisibleOnlyToAdminAndDoNotStoreSecrets() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            using var scope = factory.Services.CreateScope();
            var credentials = scope.ServiceProvider.GetRequiredService<SourceCredentials>();
            var source = await credentials.IssueAsync("private source", token);
            var old = await credentials.AuthenticateVersionedAsync(source.Secret, token);
            Assert.IsNotNull(old);
            var diagnostics = factory.Services.GetRequiredService<ReceptionRejectionDiagnostics>();
            var reader = scope.ServiceProvider.GetRequiredService<ReceptionRejectionReader>();
            var initial = await reader.ListAsync(token);
            Assert.HasCount(3, initial);
            Assert.IsTrue(initial.All(x => x.Count == 0));

            var options = new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false };
            using var client = factory.CreateClient(options);
            const string url = "/api/v1/otlp/v1/logs";
            using (var denied = await client.PostAsync(url, null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-secret-sentinel");
            using (var denied = await client.PostAsync(url, null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", source.Secret);
            var resource = new Resource();
            resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "pending-private-name" } });
            resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var request = new ExportLogsServiceRequest { ResourceLogs = { new ResourceLogs { Resource = resource, ScopeLogs = { new ScopeLogs { LogRecords = { new LogRecord { Body = new AnyValue { StringValue = "private-payload-sentinel" } } } } } } } };
            using (var body = new ByteArrayContent(request.ToByteArray()))
            {
                body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var partial = await client.PostAsync(url, body, token);
                Assert.AreEqual(HttpStatusCode.OK, partial.StatusCode);
                Assert.AreEqual(1L, ExportLogsServiceResponse.Parser.ParseFrom(await partial.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedLogRecords);
            }
            await diagnostics.FlushAsync(token);
            Assert.AreEqual(source.SourceId, (await reader.ListAsync(token)).Single(x => x.Kind == 3).ExampleSourceId);
            var discovery = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
            var candidate = (await discovery.ListAsync(1, token)).Single();
            var approved = await scope.ServiceProvider.GetRequiredService<ServiceApproval>().ApproveAsync(candidate.Id, Guid.CreateVersion7(), token);
            Assert.AreEqual(ApprovalStatus.Approved, approved.Status);
            request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].TimeUnixNano = 1;
            using (var body = new ByteArrayContent(request.ToByteArray()))
            {
                body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var oldTimestamp = await client.PostAsync(url, body, token);
                Assert.AreEqual(1L, ExportLogsServiceResponse.Parser.ParseFrom(await oldTimestamp.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedLogRecords);
            }
            await diagnostics.FlushAsync(token);
            Assert.AreEqual(1L, (await reader.ListAsync(token)).Single(x => x.Kind == 3).Count, "A rejected timestamp is not a service authorization failure.");
            request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].TimeUnixNano = 0;
            string version;
            await using (var db = await dbFactory.CreateDbContextAsync(token))
                version = Convert.ToBase64String((await db.Services.SingleAsync(x => x.Id == approved.ServiceId, token)).Version);
            Assert.AreEqual(ServiceCollectionStatus.Updated, await scope.ServiceProvider.GetRequiredService<ServiceCollectionAdministration>()
                .SetStoppedAsync(approved.ServiceId!.Value, version, true, Guid.CreateVersion7(), token));
            using (var body = new ByteArrayContent(request.ToByteArray()))
            {
                body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var stopped = await client.PostAsync(url, body, token);
                Assert.AreEqual(1L, ExportLogsServiceResponse.Parser.ParseFrom(await stopped.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedLogRecords);
            }
            await diagnostics.FlushAsync(token);
            Assert.AreEqual(2L, (await reader.ListAsync(token)).Single(x => x.Kind == 3).Count);
            var otherSource = await credentials.IssueAsync("other source", token);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherSource.Secret);
            using (var body = new ByteArrayContent(request.ToByteArray()))
            {
                body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var other = await client.PostAsync(url, body, token);
                Assert.AreEqual(1L, ExportLogsServiceResponse.Parser.ParseFrom(await other.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedLogRecords);
            }
            var nowNanos = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var metrics = new ExportMetricsServiceRequest { ResourceMetrics = { new ResourceMetrics { Resource = resource, ScopeMetrics = { new ScopeMetrics { Metrics = { new Metric { Name = "count", Gauge = new Gauge { DataPoints = { new NumberDataPoint { AsInt = 1, TimeUnixNano = nowNanos } } } } } } } } } };
            using (var body = new ByteArrayContent(metrics.ToByteArray()))
            {
                body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var response = await client.PostAsync("/api/v1/otlp/v1/metrics", body, token);
                Assert.AreEqual(1L, ExportMetricsServiceResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedDataPoints);
            }
            var span = new OpenTelemetry.Proto.Trace.V1.Span { TraceId = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 16).ToArray()), SpanId = ByteString.CopyFrom(Enumerable.Repeat((byte)2, 8).ToArray()), StartTimeUnixNano = nowNanos, EndTimeUnixNano = nowNanos };
            var traces = new ExportTraceServiceRequest { ResourceSpans = { new OpenTelemetry.Proto.Trace.V1.ResourceSpans { Resource = resource, ScopeSpans = { new OpenTelemetry.Proto.Trace.V1.ScopeSpans { Spans = { span } } } } } };
            using (var body = new ByteArrayContent(traces.ToByteArray()))
            {
                body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var response = await client.PostAsync("/api/v1/otlp/v1/traces", body, token);
                Assert.AreEqual(1L, ExportTraceServiceResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedSpans);
            }
            await diagnostics.FlushAsync(token);
            var example = (await reader.ListAsync(token)).Single(x => x.Kind == 3);
            Assert.AreEqual(5L, example.Count);
            Assert.AreEqual(otherSource.SourceId, example.ExampleSourceId);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", source.Secret);
            var admin = scope.ServiceProvider.GetRequiredService<SourceAdministration>();
            Assert.IsTrue(await admin.RevokeAsync(source.SourceId, Guid.CreateVersion7(), token));
            using (var denied = await client.PostAsync(url, null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);

            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, old.SourceId.ToString("D")),
                new Claim("vigiloom:source-version", Convert.ToBase64String(old.Version))], "VigiloomServiceSource"));
            var metadata = new DefaultHttpContext { User = principal, RequestServices = factory.Services };
            metadata.Request.ContentType = "application/json";
            metadata.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"serviceKey\":\"orders\",\"environment\":\"prod\",\"displayName\":\"Orders\"}"));
            metadata.Response.Body = new MemoryStream();
            var result = await ServiceObservationEndpoint.ReceiveAsync(metadata, scope.ServiceProvider.GetRequiredService<CandidateDiscovery>(), token, diagnostics);
            await result.ExecuteAsync(metadata);
            Assert.AreEqual(StatusCodes.Status401Unauthorized, metadata.Response.StatusCode);

            Parallel.For(0, 1000, _ => diagnostics.Record(ReceptionRejectionKind.ServiceNotAuthorized, ReceptionIngress.Otlp, source.SourceId));
            await diagnostics.FlushAsync(token);
            var rows = await reader.ListAsync(token);
            Assert.AreEqual(3L, rows.Single(x => x.Kind == 1).Count);
            Assert.AreEqual(1L, rows.Single(x => x.Kind == 2).Count);
            Assert.AreEqual(1005L, rows.Single(x => x.Kind == 3).Count);
            Assert.IsNull(rows.Single(x => x.Kind == 1).ExampleSourceId);
            Assert.AreEqual(source.SourceId, rows.Single(x => x.Kind == 2).ExampleSourceId);
            Assert.AreEqual(source.SourceId, rows.Single(x => x.Kind == 3).ExampleSourceId);
            Assert.AreEqual("private source", rows.Single(x => x.Kind == 3).ExampleSourceName);
            Assert.AreEqual((byte)ReceptionIngress.Discovery, rows.Single(x => x.Kind == 2).ExampleIngress);
            Assert.AreEqual((byte)ReceptionIngress.Otlp, rows.Single(x => x.Kind == 3).ExampleIngress);
            Assert.IsTrue(rows.All(x => x.FirstRecordedAt is not null && x.LastRecordedAt >= x.FirstRecordedAt));
            await using (var db = await dbFactory.CreateDbContextAsync(token)) Assert.AreEqual(0, await db.LogRecords.CountAsync(token));

            using var viewer = factory.CreateClient(options);
            using (var denied = await viewer.GetAsync("/settings/service-reception", token)) Assert.AreNotEqual(HttpStatusCode.OK, denied.StatusCode);
            using var readOnly = factory.CreateClient(options);
            await LoginAsync(readOnly, "reader", token);
            using (var denied = await readOnly.GetAsync("/settings/service-reception", token)) Assert.AreNotEqual(HttpStatusCode.OK, denied.StatusCode);
            await LoginAsync(viewer, "admin", token);
            using var page = await viewer.GetAsync("/settings/service-reception", token);
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            var text = (await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
            Assert.Contains("接收拒絕摘要", text);
            Assert.Contains("1005 次", text);
            Assert.Contains("代表來源：private source", text);
            Assert.Contains("不能代表完整次數或攻擊次數", text);
            Assert.DoesNotContain(source.Secret, text);
            Assert.DoesNotContain("invalid-secret-sentinel", text);
            Assert.DoesNotContain("private-payload-sentinel", text);
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
}
