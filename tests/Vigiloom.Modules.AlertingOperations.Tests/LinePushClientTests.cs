using System.Net;
using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Modules.AlertingOperations.Tests;

[TestClass]
public sealed class LinePushClientTests
{
    [TestMethod]
    [DataRow(200, false, LinePushResult.Accepted)]
    [DataRow(409, true, LinePushResult.Accepted)]
    [DataRow(409, false, LinePushResult.Rejected)]
    [DataRow(401, false, LinePushResult.Rejected)]
    [DataRow(429, false, LinePushResult.Retry)]
    [DataRow(503, false, LinePushResult.Retry)]
    public async Task SendUsesRetryKeyAndClassifiesResponse(int code, bool acceptedHeader, LinePushResult expected)
    {
        var handler = new RecordingHandler((HttpStatusCode)code, acceptedHeader);
        using var http = new HttpClient(handler);
        var client = new LinePushClient(http);
        var id = Guid.CreateVersion7();
        var result = await client.SendAsync("test-token", "C123", "service restored", id, CancellationToken.None);
        Assert.AreEqual(expected, result);
        Assert.AreEqual(id.ToString("D"), handler.RetryKey);
        Assert.AreEqual("Bearer test-token", handler.Authorization);
        Assert.Contains("service restored", handler.Body!);
        Assert.Contains("C123", handler.Body!);
    }

    private sealed class RecordingHandler(HttpStatusCode code, bool acceptedHeader) : HttpMessageHandler
    {
        public string? RetryKey { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RetryKey = request.Headers.GetValues("X-Line-Retry-Key").Single();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(code);
            if (acceptedHeader) response.Headers.Add("X-Line-Accepted-Request-Id", "accepted");
            return response;
        }
    }
}
