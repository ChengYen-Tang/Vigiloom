extern alias HostMonitoring;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Sink = HostMonitoring::Vigiloom.HostMonitoring.OperationsLogSink;

namespace Vigiloom.Gateway.Tests;

[TestClass]
public sealed class OperationsLogSinkTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(503, "{}", LogReceiptStatus.Busy)]
    [DataRow(200, "{}", LogReceiptStatus.Busy)]
    [DataRow(503, "{\"status\":0}", LogReceiptStatus.Busy)]
    [DataRow(200, "{\"status\":0}", LogReceiptStatus.Accepted)]
    public async Task OnlyAcceptsConsistentExplicitReceipt(int status, string body, LogReceiptStatus expected)
    {
        using var client = new HttpClient(new ResponseHandler(status, body)) { BaseAddress = new Uri("http://localhost/") };
        var sink = new Sink(client, NullLogger<Sink>.Instance);
        var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        var now = DateTimeOffset.UtcNow;
        var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
            Guid.CreateVersion7(), AgentLogSourceKind.Journal, "System", 1,
            [new AgentLogRecord(Guid.CreateVersion7(), now, "message", null, null, null, null, null)]);
        Assert.AreEqual(expected, (await sink.ReceiveAsync(identity, batch, TestContext.CancellationToken)).Status);
    }

    private sealed class ResponseHandler(int status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
    }
}
