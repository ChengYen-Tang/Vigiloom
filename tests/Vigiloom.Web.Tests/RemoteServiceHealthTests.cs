using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Services;
using Vigiloom.Web.ServiceDetails;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteServiceHealthTests(TestContext context)
{
    [TestMethod]
    public async Task ReadsStatusAndHistoryFromObservationService()
    {
        var id = Guid.CreateVersion7();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var end = DateTimeOffset.UtcNow;
        using var client = new HttpClient(new Handler(id, start, end)) { BaseAddress = new Uri("http://observability/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Services:Observability:Url"] = "http://observability" }).Build();
        var query = new ServiceQueries(client, config, null!, null!, null!);
        Assert.AreEqual(id, (await query.DetailAsync(id, context.CancellationToken))!.Id);
        Assert.IsEmpty((await query.InstancesAsync(id, 2, context.CancellationToken))!.Items);
        Assert.AreEqual(ServiceHealthState.Healthy, (await query.StatusAsync(id, context.CancellationToken))!.State);
        Assert.AreEqual(1.0, (await query.HistoryAsync(id, start, end, context.CancellationToken))!.Coverage);
    }

    private sealed class Handler(Guid id, DateTimeOffset start, DateTimeOffset end) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object result;
            if (request.RequestUri!.AbsolutePath == $"/internal/v1/services/{id:D}")
                result = new Vigiloom.Contracts.Services.ServiceDetails(id, "API", "api", "", "prod", start, false, false, null, null, null, end);
            else if (request.RequestUri.AbsolutePath == $"/internal/v1/services/{id:D}/instances")
            {
                Assert.AreEqual("2", QueryHelpers.ParseQuery(request.RequestUri.Query)["page"].ToString());
                result = new ServiceInstancePage([], false);
            }
            else if (request.RequestUri.AbsolutePath == $"/internal/v1/health/{id:D}/status")
                result = new ServiceHealthStatus(ServiceHealthState.Healthy, 1, 0);
            else
            {
                Assert.AreEqual($"/internal/v1/health/{id:D}/history", request.RequestUri.AbsolutePath);
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                Assert.AreEqual(start, DateTimeOffset.Parse(query["from"].ToString()));
                Assert.AreEqual(end, DateTimeOffset.Parse(query["to"].ToString()));
                result = new ServiceHealthHistory([], 1, 1);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}
