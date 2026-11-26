using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Web.Operations;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteNotificationReaderTests(TestContext context)
{
    [TestMethod]
    public async Task PassesFiltersToNotificationsAndAlerts()
    {
        var query = new NotificationQuery(2, "ping", new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 4));
        using var client = new HttpClient(new Handler()) { BaseAddress = new Uri("http://operations/") };
        var remote = new RemoteNotificationReader(client);
        Assert.AreEqual(2, (await remote.ReadAsync<NotificationPage>("notifications", query, context.CancellationToken)).Page);
        Assert.AreEqual(2, (await remote.ReadAsync<AlertEpisodePage>("alerts", query, context.CancellationToken)).Page);
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            Assert.AreEqual("2", query["page"].ToString());
            Assert.AreEqual("ping", query["source"].ToString());
            Assert.AreEqual("2026-01-02", query["start"].ToString());
            Assert.AreEqual("2026-01-04", query["end"].ToString());
            object response = request.RequestUri.AbsolutePath switch
            {
                "/internal/v1/history/notifications" => new NotificationPage([], 2, false),
                "/internal/v1/history/alerts" => new AlertEpisodePage([], 2, false),
                _ => throw new InvalidOperationException("Unexpected notification path.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
        }
    }
}
