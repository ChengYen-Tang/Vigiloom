using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Web.Diagnostics;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteJobOverviewTests(TestContext context)
{
    [TestMethod]
    public async Task ReadsOverviewFromOperations()
    {
        using var client = new HttpClient(new Handler()) { BaseAddress = new Uri("http://operations/") };
        var result = await new RemoteJobOverview(client).GetAsync(context.CancellationToken);
        Assert.AreEqual(2L, result.Workers);
        Assert.AreEqual(3, result.PendingMessages);
        var remote = new RemoteJobOverview(client);
        Assert.IsTrue(await remote.DatabaseHealthyAsync(context.CancellationToken));
        Assert.HasCount(1, (await remote.SchedulerAsync(context.CancellationToken)).Values);
        var worker = Assert.ContainsSingle((await remote.WorkersAsync(context.CancellationToken)).Workers);
        Assert.AreEqual(Vigiloom.Modules.AlertingOperations.Application.ScannerFailureKind.Timeout, worker.Observation.Speed.FailureKind);
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object result = request.RequestUri!.AbsolutePath switch
            {
                "/internal/v1/diagnostics/jobs" => new WorkOverview(2, 0, 0, 0, 0, 3, 0, []),
                "/health/ready" => new { },
                "/internal/v1/diagnostics/scheduler" => new SchedulerHeartbeatsInfo(DateTimeOffset.UtcNow, [DateTimeOffset.UtcNow]),
                "/internal/v1/diagnostics/workers" => new WorkerDiagnosticsInfo(DateTimeOffset.UtcNow,
                    [new(Guid.CreateVersion7(), null, null, new(null, null, 0, ScannerDiagnosticFailure.None), new(null, null, 1, ScannerDiagnosticFailure.Timeout))]),
                _ => throw new InvalidOperationException("Unexpected diagnostics path.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}
