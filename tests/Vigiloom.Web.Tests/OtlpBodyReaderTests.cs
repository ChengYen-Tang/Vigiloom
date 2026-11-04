using System.IO.Compression;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Web.Telemetry;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class OtlpBodyReaderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadsProtobufWithAndWithoutGzip(bool gzip)
    {
        var original = new ExportLogsServiceRequest();
        original.ResourceLogs.Add(new ResourceLogs());
        var bytes = original.ToByteArray();
        var request = Request(gzip ? Compress(bytes) : bytes, gzip);
        var result = await OtlpBodyReader.ReadAsync(request, ExportLogsServiceRequest.Parser, CancellationToken.None);
        Assert.AreEqual(OtlpBodyStatus.Success, result.Status);
        Assert.AreEqual(original, result.Message);
    }

    [TestMethod]
    public async Task RejectsMalformedAndTruncatedInput()
    {
        var malformed = await OtlpBodyReader.ReadAsync(Request([0x0a, 0xff], false), ExportLogsServiceRequest.Parser, CancellationToken.None);
        Assert.AreEqual(OtlpBodyStatus.Invalid, malformed.Status);
        var gzip = Compress([0x0a, 0x00]);
        var truncated = await OtlpBodyReader.ReadAsync(Request(gzip[..^4], true), ExportLogsServiceRequest.Parser, CancellationToken.None);
        Assert.AreEqual(OtlpBodyStatus.Invalid, truncated.Status);
    }

    [TestMethod]
    public async Task BoundsWireAndDecompressedBodies()
    {
        var large = new byte[OtlpBodyReader.MaximumBytes + 1];
        var wire = await OtlpBodyReader.ReadAsync(Request(large, false), ExportLogsServiceRequest.Parser, CancellationToken.None);
        var decoded = await OtlpBodyReader.ReadAsync(Request(Compress(large), true), ExportLogsServiceRequest.Parser, CancellationToken.None);
        Assert.AreEqual(OtlpBodyStatus.TooLarge, wire.Status);
        Assert.AreEqual(OtlpBodyStatus.TooLarge, decoded.Status);
    }

    [TestMethod]
    public async Task RejectsChecksumAndUnsupportedEncodings()
    {
        var corrupt = Compress([0x0a, 0x00]);
        corrupt[^8] ^= 0x01;
        var checksum = await OtlpBodyReader.ReadAsync(Request(corrupt, true), ExportLogsServiceRequest.Parser, CancellationToken.None);
        var unsupportedRequest = Request([], false);
        unsupportedRequest.Headers.ContentEncoding = "br";
        var unsupported = await OtlpBodyReader.ReadAsync(unsupportedRequest, ExportLogsServiceRequest.Parser, CancellationToken.None);
        Assert.AreEqual(OtlpBodyStatus.Invalid, checksum.Status);
        Assert.AreEqual(OtlpBodyStatus.UnsupportedEncoding, unsupported.Status);
    }

    [TestMethod]
    public async Task PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            OtlpBodyReader.ReadAsync(Request([1], false), ExportLogsServiceRequest.Parser, cancellation.Token));
    }

    [TestMethod]
    public async Task RejectsObjectExpansionBeforeParsing()
    {
        // Two bytes per empty ResourceLogs would otherwise expand into 100,001 objects.
        var payload = new byte[200_002];
        for (var i = 0; i < payload.Length; i += 2) payload[i] = 0x0a;
        var result = await OtlpBodyReader.ReadAsync(Request(payload, false), ExportLogsServiceRequest.Parser, CancellationToken.None);
        Assert.AreEqual(OtlpBodyStatus.Invalid, result.Status);
    }

    [TestMethod]
    public async Task PreservesUnknownProtobufFields()
    {
        // Unknown field 9, length-delimited: arbitrary data is not another protobuf message.
        var result = await OtlpBodyReader.ReadAsync(Request([0x4a, 0x02, 0xff, 0xff], false), ExportLogsServiceRequest.Parser, CancellationToken.None);
        Assert.AreEqual(OtlpBodyStatus.Success, result.Status);
        CollectionAssert.AreEqual(new byte[] { 0x4a, 0x02, 0xff, 0xff }, result.Message!.ToByteArray());
    }

    [TestMethod]
    public async Task AllowsPackedHistogramValuesAndBoundsNestedAttributes()
    {
        var point = new HistogramDataPoint { BucketCounts = { 1, 2, 3 }, ExplicitBounds = { 0.5, 1.5 } };
        var metric = new Metric { Name = "histogram", Histogram = new Histogram { DataPoints = { point } } };
        var metrics = new ExportMetricsServiceRequest { ResourceMetrics = { new ResourceMetrics
            { ScopeMetrics = { new ScopeMetrics { Metrics = { metric } } } } } };
        var result = await OtlpBodyReader.ReadAsync(Request(metrics.ToByteArray(), false), ExportMetricsServiceRequest.Parser, CancellationToken.None);
        Assert.AreEqual(OtlpBodyStatus.Success, result.Status);
        Assert.AreEqual(metrics, result.Message);

        static ExportLogsServiceRequest Nested(int depth)
        {
            var value = new AnyValue { StringValue = "leaf" };
            for (var i = 0; i < depth; i++) value = new AnyValue { ArrayValue = new ArrayValue { Values = { value } } };
            return new ExportLogsServiceRequest { ResourceLogs = { new ResourceLogs { Resource = new Resource
                { Attributes = { new KeyValue { Key = "nested", Value = value } } } } } };
        }
        var shallow = await OtlpBodyReader.ReadAsync(Request(Nested(5).ToByteArray(), false), ExportLogsServiceRequest.Parser, CancellationToken.None);
        var deep = await OtlpBodyReader.ReadAsync(Request(Nested(20).ToByteArray(), false), ExportLogsServiceRequest.Parser, CancellationToken.None);
        Assert.AreEqual(OtlpBodyStatus.Success, shallow.Status);
        Assert.AreEqual(OtlpBodyStatus.Invalid, deep.Status);
    }

    private static HttpRequest Request(byte[] bytes, bool gzip)
    {
        var request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(bytes);
        if (gzip) request.Headers.ContentEncoding = "gzip";
        return request;
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
}
