using Microsoft.AspNetCore.Http;
using OpenTelemetry.Proto.Collector.Logs.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Web.Telemetry;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class OtlpResponseTests
{
    [TestMethod]
    public async Task SuccessfulAndPartialResponsesUseOfficialMessages()
    {
        var result = new ExportResult(ExportStatus.Partial, 3);
        var context = Context();
        await OtlpResponse.FromResult(context, result, x => new ExportLogsServiceResponse
        {
            PartialSuccess = new ExportLogsPartialSuccess { RejectedLogRecords = x.Rejected, ErrorMessage = "Some records were rejected." }
        }).ExecuteAsync(context);
        Assert.AreEqual(200, context.Response.StatusCode);
        Assert.AreEqual(OtlpResponse.ContentType, context.Response.ContentType);
        var response = ExportLogsServiceResponse.Parser.ParseFrom(((MemoryStream)context.Response.Body).ToArray());
        Assert.AreEqual(3L, response.PartialSuccess.RejectedLogRecords);
    }

    [TestMethod]
    public async Task RetryAndUnsendableBatchHaveDifferentStatuses()
    {
        var retry = Context();
        await OtlpResponse.FromResult(retry, new(ExportStatus.Retry, 0, TimeSpan.FromSeconds(2)), _ => new ExportLogsServiceResponse()).ExecuteAsync(retry);
        Assert.AreEqual(503, retry.Response.StatusCode);
        Assert.AreEqual("2", retry.Response.Headers.RetryAfter.ToString());
        Assert.IsNotEmpty(Google.Rpc.Status.Parser.ParseFrom(((MemoryStream)retry.Response.Body).ToArray()).Message);
        var tooLarge = Context();
        await OtlpResponse.FromResult(tooLarge, new(ExportStatus.BatchTooLarge, 0), _ => new ExportLogsServiceResponse()).ExecuteAsync(tooLarge);
        Assert.AreEqual(400, tooLarge.Response.StatusCode);
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }
}
