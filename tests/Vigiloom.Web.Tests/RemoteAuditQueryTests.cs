using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Operations;
using Vigiloom.Web.Operations;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteAuditQueryTests(TestContext context)
{
    [TestMethod]
    public async Task PreservesCategoryAndRange()
    {
        var from = DateTimeOffset.UtcNow.AddHours(-1);
        var query = new OperationAuditQuery(from, from.AddHours(1), 2);
        using var client = new HttpClient(new Handler(query)) { BaseAddress = new Uri("http://operations/") };
        Assert.AreEqual(2, (await new RemoteAuditQuery(client).ReadAsync("ping-settings", query, context.CancellationToken)).Page);
    }

    private sealed class Handler(OperationAuditQuery expected) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("/internal/v1/audit/ping-settings", request.RequestUri!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.AreEqual(expected.From, DateTimeOffset.Parse(query["from"].ToString(), System.Globalization.CultureInfo.InvariantCulture));
            Assert.AreEqual(expected.To, DateTimeOffset.Parse(query["to"].ToString(), System.Globalization.CultureInfo.InvariantCulture));
            Assert.AreEqual("2", query["page"].ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new OperationAuditPage([], 2, false, false)) });
        }
    }
}
