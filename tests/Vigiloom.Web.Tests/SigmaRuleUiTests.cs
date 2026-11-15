using System.Net;
using System.Text;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SigmaRuleUiTests(TestContext context)
{
    [TestMethod]
    public Task SelectSamplesAcrossPages_PreservesSelectionsAndPostsThem() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var security = factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>();
            Assert.AreEqual(SigmaRefreshResult.Staged, await new SigmaRefresh(security, new SourceClients(), TimeProvider.System).RunDueAsync(token));
            await using (var db = await security.CreateDbContextAsync(token))
            {
                var source = await db.IntelligenceSources.SingleAsync(x => x.Key == "sigmahq-rules", token);
                var revision = await db.IntelligenceRevisions.SingleAsync(x => x.SourceId == source.Id, token);
                Assert.AreEqual(IntelligencePublicationResult.Published,
                    await new IntelligencePublication(security, new OperationAuditWriter(), TimeProvider.System)
                        .ActivateAsync(source.Id, revision.Id, null, "", false, Guid.CreateVersion7(), token));
            }
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var device = new Device("Test server", "127.0.0.5", true, now);
            var otherDevice = new Device("Other server", "127.0.0.6", true, now);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            { db.Devices.AddRange(device, otherDevice); await db.SaveChangesAsync(token); }
            var secret = RandomNumberGenerator.GetBytes(32);
            var agent = new Vigiloom.Modules.AgentManagement.Domain.Agent(device.Id, device.AgentAuthorizationGeneration, SHA256.HashData(secret), now);
            var otherAgent = new Vigiloom.Modules.AgentManagement.Domain.Agent(otherDevice.Id, 1, new byte[32], now);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            {
                db.Agents.AddRange(agent, otherAgent);
                var records = Enumerable.Range(0, 26).Select(i => new AgentLogRecord(Guid.CreateVersion7(), now,
                    $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>{(i == 25 ? 4625 : 4624)}</EventID><Channel>Security</Channel></System></Event>",
                    null, null, null, null, null)).ToArray();
                var batch = new AgentLogBatch(3, Guid.CreateVersion7(), now, Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent, "security", 1, records);
                for (var i = 0; i < records.Length; i++) db.HostLogs.Add(new HostLog(agent.Id, batch, i, now));
                var otherBatch = new AgentLogBatch(3, Guid.CreateVersion7(), now, Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent, "other", 1, [records[0] with { MessageId = Guid.CreateVersion7() }]);
                db.HostLogs.Add(new HostLog(otherAgent.Id, otherBatch, 0, now));
                await db.SaveChangesAsync(token);
            }
            using var client = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(client, "admin", token);
            var url = $"/settings/sigma/rule?path=rules%2Ftest.yml&device={device.Id:D}";
            using var first = await client.GetAsync(url, token);
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            var doc = await new HtmlParser().ParseDocumentAsync(await first.Content.ReadAsStringAsync(token), token);
            var form = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "sigma-rule-test");
            var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            var firstId = fields["TestInput.Samples[0].MessageId"];
            fields["TestInput.Samples[0].Expected"] = "Match";
            fields["TestInput.Action"] = "next";
            using var next = await client.PostAsync(url, new FormUrlEncodedContent(fields), token);
            Assert.AreEqual(HttpStatusCode.OK, next.StatusCode, await next.Content.ReadAsStringAsync(token));
            var nextDoc = await new HtmlParser().ParseDocumentAsync(await next.Content.ReadAsStringAsync(token), token);
            Assert.Contains("已選案例", nextDoc.Body!.TextContent);
            var nextForm = nextDoc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "sigma-rule-test");
            var nextFields = nextForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            Assert.AreEqual(firstId, nextFields["TestInput.Selected[0].MessageId"]);
            Assert.AreNotEqual(firstId, nextFields["TestInput.Samples[0].MessageId"]);
            nextFields["TestInput.Samples[0].Expected"] = "NoMatch";
            nextFields["TestInput.NextDevice"] = otherDevice.Id.ToString("D");
            nextFields["TestInput.Action"] = "switch";
            using var switched = await client.PostAsync(url, new FormUrlEncodedContent(nextFields), token);
            Assert.AreEqual(HttpStatusCode.OK, switched.StatusCode);
            var switchedDoc = await new HtmlParser().ParseDocumentAsync(await switched.Content.ReadAsStringAsync(token), token);
            var switchedForm = switchedDoc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "sigma-rule-test");
            var switchedFields = switchedForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            Assert.AreEqual(firstId, switchedFields["TestInput.Selected[0].MessageId"]);
            Assert.IsTrue(switchedFields.ContainsKey("TestInput.Selected[1].MessageId"));
            Assert.AreEqual(otherDevice.Id.ToString("D"), switchedFields["TestInput.DeviceId"]);
            switchedFields["TestInput.RangeHours"] = "168";
            switchedFields["TestInput.Action"] = "range";
            using var range = await client.PostAsync(url, new FormUrlEncodedContent(switchedFields), token);
            Assert.AreEqual(HttpStatusCode.OK, range.StatusCode);
            var rangeDoc = await new HtmlParser().ParseDocumentAsync(await range.Content.ReadAsStringAsync(token), token);
            var rangeForm = rangeDoc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "sigma-rule-test");
            var rangeFields = rangeForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            Assert.AreEqual(firstId, rangeFields["TestInput.Selected[0].MessageId"]);
            Assert.IsTrue(rangeFields.ContainsKey("TestInput.Selected[1].MessageId"));
            rangeFields["TestInput.RangeHours"] = "168";
            rangeFields["TestInput.Action"] = "run";
            var matchExpected = rangeFields["TestInput.Selected[0].Expected"];
            rangeFields["TestInput.Selected[0].Expected"] = "NoMatch";
            using var failedRun = await client.PostAsync(url, new FormUrlEncodedContent(rangeFields), token);
            Assert.AreEqual(HttpStatusCode.OK, failedRun.StatusCode);
            var failedDoc = await new HtmlParser().ParseDocumentAsync(await failedRun.Content.ReadAsStringAsync(token), token);
            Assert.Contains("試跑未通過", failedDoc.Body!.TextContent);
            Assert.Contains("預期 不應命中，實際 應命中", failedDoc.Body.TextContent);
            rangeFields["TestInput.Selected[0].Expected"] = matchExpected;
            using var run = await client.PostAsync(url, new FormUrlEncodedContent(rangeFields), token);
            Assert.AreEqual(HttpStatusCode.OK, run.StatusCode);
            var runDoc = await new HtmlParser().ParseDocumentAsync(await run.Content.ReadAsStringAsync(token), token);
            Assert.Contains("試跑通過", runDoc.Body!.TextContent);
            Assert.Contains("預期 應命中，實際 應命中", runDoc.Body.TextContent);
            Assert.Contains("預期 不應命中，實際 不應命中", runDoc.Body.TextContent);
            var stateForm = runDoc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "sigma-rule-state");
            var stateFields = stateForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            using var enabled = await client.PostAsync(url, new FormUrlEncodedContent(stateFields), token);
            Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode);
            var enabledDoc = await new HtmlParser().ParseDocumentAsync(await enabled.Content.ReadAsStringAsync(token), token);
            Assert.AreEqual("規則已啟用。", enabledDoc.QuerySelector("[role='status']")?.TextContent.Trim(), enabledDoc.Body!.TextContent);
            rangeFields["TestInput.Exceptions"] = "EventID=0";
            using var adjusted = await client.PostAsync(url, new FormUrlEncodedContent(rangeFields), token);
            Assert.AreEqual(HttpStatusCode.OK, adjusted.StatusCode);
            var adjustedDoc = await new HtmlParser().ParseDocumentAsync(await adjusted.Content.ReadAsStringAsync(token), token);
            Assert.Contains("試跑通過", adjustedDoc.Body!.TextContent);
            Assert.Contains("目前生效的排除條件", adjustedDoc.Body.TextContent);
            Assert.Contains("無。", adjustedDoc.Body.TextContent);
            var applyForm = adjustedDoc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "sigma-rule-state");
            Assert.IsNotNull(applyForm.QuerySelector("button[name='StateInput.Apply']"));
            var applyFields = applyForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            applyFields["StateInput.Apply"] = "true";
            using var applied = await client.PostAsync(url, new FormUrlEncodedContent(applyFields), token);
            Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode);
            var appliedDoc = await new HtmlParser().ParseDocumentAsync(await applied.Content.ReadAsStringAsync(token), token);
            Assert.Contains("EventID = 0", appliedDoc.Body!.TextContent);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var liveRecord = new AgentLogRecord(Guid.CreateVersion7(), DateTimeOffset.UtcNow,
                    "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>4625</EventID><Channel>Security</Channel></System></Event>",
                    null, null, null, null, null);
                var liveBatch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), DateTimeOffset.UtcNow,
                    Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent, "security", 1, [liveRecord]);
                var credential = $"vla_{agent.Id:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
                var receiver = scope.ServiceProvider.GetRequiredService<AgentBatchReceiver>();
                var livePayload = AgentLogBatchCodec.Serialize(liveBatch);
                Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, livePayload, token));
                Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, livePayload, token));
                var dispatcher = new OutboxDispatcher(database.ConnectionString, scope.ServiceProvider.GetServices<IOutboxHandler>(), NullLogger<OutboxDispatcher>.Instance);
                await dispatcher.DispatchAsync(token);
                await using var detections = await security.CreateDbContextAsync(token);
                var match = await detections.SigmaDetectionResults.AsNoTracking().SingleAsync(x => x.MessageId == liveRecord.MessageId, token);
                Assert.AreEqual((int)SigmaExpectedOutcome.Match, match.Result);
                Assert.AreEqual(agent.Id, match.AgentId);
            }
            await using var check = await security.CreateDbContextAsync(token);
            Assert.IsTrue((await check.SigmaRuleActivations.AsNoTracking().SingleAsync(token)).Enabled);
        }, context.CancellationToken);

    private sealed class SourceClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SourceHandler());
    }

    private sealed class SourceHandler : HttpMessageHandler
    {
        private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{Commit}\"}}") });
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, leaveOpen: true))
            {
                tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry([new("comment", Commit)]));
                Write($"sigma-{Commit}/LICENSE", "# Licenses\n\nThe content of this repository is released under the following licenses:\n\n- The Sigma specification (https://github.com/SigmaHQ/sigma-specification) and the Sigma logo are public domain\n- The rules contained in the SigmaHQ repository (https://github.com/SigmaHQ) are released under the [Detection Rule License (DRL) 1.1](https://github.com/SigmaHQ/Detection-Rule-License)\n");
                Write($"sigma-{Commit}/rules/test.yml", "title: Test rule\nauthor: author\nlogsource: {product: windows, service: security}\ndetection:\n  selection: {EventID: 4625}\n  condition: selection\n");
                void Write(string path, string value)
                {
                    using var content = new MemoryStream(Encoding.UTF8.GetBytes(value));
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = content });
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) });
        }
    }
}
