using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Operations;
using Vigiloom.Web.Events;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteTimelineReaderTests(TestContext context)
{
    [TestMethod]
    public async Task QueryAndAuditUseOperationsContracts()
    {
        var group = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var query = new EventTimelineQuery(now.AddHours(-1), now, group, "host-log", 2);
        var actor = Guid.CreateVersion7();
        using var client = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var args = QueryHelpers.ParseQuery(request.RequestUri!.Query);
                Assert.AreEqual(group.ToString("D"), args["groupId"].ToString());
                Assert.AreEqual("host-log", args["source"].ToString());
                Assert.AreEqual("2", args["page"].ToString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new EventTimelinePage([], 2, false, false)) };
            }
            Assert.AreEqual("/internal/v1/audit/event-access", request.RequestUri!.AbsolutePath);
            var entry = await request.Content!.ReadFromJsonAsync<OperationAudit>(context.CancellationToken);
            Assert.IsNotNull(entry);
            Assert.AreEqual(actor, entry.ActorId);
            Assert.IsInstanceOfType<EventTimelineAccessed>(entry.Details);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        })) { BaseAddress = new Uri("http://operations/") };
        var reader = new RemoteTimelineReader(client);
        Assert.AreEqual(2, (await reader.GetAsync(query, context.CancellationToken)).Page);
        await reader.RecordAsync(new(actor, actor, now, new EventTimelineAccessed(query.From, query.Until, group, query.Source, 2, 0, false)), context.CancellationToken);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
