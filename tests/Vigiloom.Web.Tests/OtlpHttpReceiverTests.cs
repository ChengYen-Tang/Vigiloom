using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Proto.Collector.Logs.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Web.Telemetry;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class OtlpHttpReceiverTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public async Task UsesAuthenticatedSourceAndAcknowledgesOnlyAfterExport()
    {
        var sourceId = Guid.CreateVersion7();
        var context = Context(sourceId);
        var called = false;
        var result = await OtlpHttpReceiver.ReceiveAsync(context, ExportLogsServiceRequest.Parser,
            (AuthenticatedSource actual, ExportLogsServiceRequest _, CancellationToken _) =>
            {
                called = true;
                Assert.AreEqual(sourceId, actual.SourceId);
                return Task.FromResult(new ExportResult(ExportStatus.Retry, 0, TimeSpan.FromSeconds(2)));
            }, _ => new ExportLogsServiceResponse(), CancellationToken.None);
        await result.ExecuteAsync(context);
        Assert.IsTrue(called);
        Assert.AreEqual(503, context.Response.StatusCode);
        Assert.AreEqual("2", context.Response.Headers.RetryAfter.ToString());
    }

    [TestMethod]
    public async Task RejectsUnauthenticatedAndBusyWithoutCallingExport()
    {
        var called = false;
        Task<ExportResult> Export(AuthenticatedSource source, ExportLogsServiceRequest request, CancellationToken token)
        {
            called = true;
            return Task.FromResult(new ExportResult(ExportStatus.Complete, 0));
        }
        var anonymous = Context(null);
        var denied = await OtlpHttpReceiver.ReceiveAsync(anonymous, ExportLogsServiceRequest.Parser,
            Export, _ => new ExportLogsServiceResponse(), CancellationToken.None);
        await denied.ExecuteAsync(anonymous);
        Assert.AreEqual(401, anonymous.Response.StatusCode);
        using var concurrency = new OtlpConcurrency();
        for (var i = 0; i < 4; i++) await concurrency.Gate.WaitAsync(TestContext.CancellationToken);
        var busy = Context(Guid.CreateVersion7());
        await OtlpTransportGuard.ProcessAsync(busy, _ => { called = true; return Task.CompletedTask; },
            concurrency, TimeSpan.FromSeconds(30));
        Assert.AreEqual(503, busy.Response.StatusCode);
        Assert.IsFalse(called);
    }

    [TestMethod]
    public async Task DeadlineCancelsWorkAndReleasesSlot()
    {
        using var concurrency = new OtlpConcurrency();
        var context = Context(Guid.CreateVersion7());
        var canceled = false;
        await OtlpTransportGuard.ProcessAsync(context, async c =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, c.RequestAborted); }
            catch (OperationCanceledException) { canceled = true; throw; }
        }, concurrency, TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(canceled);
        Assert.AreEqual(503, context.Response.StatusCode);
        Assert.AreEqual(4, concurrency.Gate.CurrentCount);
        Assert.AreEqual("1", context.Response.Headers.RetryAfter.ToString());
    }

    private static DefaultHttpContext Context(Guid? source)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = OtlpResponse.ContentType;
        context.Request.Body = new MemoryStream();
        context.Response.Body = new MemoryStream();
        if (source is not null)
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, source.Value.ToString()), new Claim("vigiloom:source-version", Convert.ToBase64String(new byte[8]))], "test"));
        return context;
    }
}
