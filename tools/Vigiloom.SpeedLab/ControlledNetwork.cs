using System.Collections.Concurrent;
using System.Net;

namespace Vigiloom.SpeedLab;

// This handler never delegates to a network transport. Hosts identify synthetic fixture routes only.
internal sealed class ControlledNetwork(Profile profile) : HttpMessageHandler
{
    private static readonly ConcurrentDictionary<int, Lazy<byte[]>> Payloads = new();

    public Throttle Download { get; } = new(profile.DownloadMbps * 1_000_000L);
    public Throttle Upload { get; } = new(profile.UploadMbps * 1_000_000L);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri is not { Host: "c.speedtest.net" or "www.speedtest.net" or "speedtest.example.com" } uri)
            throw new InvalidOperationException("Unexpected destination in offline fixture.");

        var path = uri.AbsolutePath;
        if (path == "/api/js/config-sdk") return new(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
        HttpContent content;
        if (path.EndsWith("speedtest-servers-static.php", StringComparison.Ordinal))
        {
            content = new StringContent("<settings><servers><server url=\"http://speedtest.example.com:8080/speedtest/upload.php\" lat=\"25\" lon=\"121\" name=\"Taipei\" country=\"Taiwan\" sponsor=\"Example\" id=\"1\" /></servers></settings>");
        }
        else if (path.EndsWith("latency.txt", StringComparison.Ordinal)) content = new StringContent("test=test");
        else if (request.Method == HttpMethod.Post)
        {
            if (request.Content is not null && request.Content.Headers.ContentLength > 0)
            {
                using var sink = new ThrottledSink(Upload, token);
                await request.Content.CopyToAsync(sink, token);
            }
            if (profile.ExtraRequestDelayMilliseconds > 0) await Task.Delay(profile.ExtraRequestDelayMilliseconds, token);
            content = new StringContent("ok");
        }
        else
        {
            if (profile.ExtraRequestDelayMilliseconds > 0) await Task.Delay(profile.ExtraRequestDelayMilliseconds, token);
            var file = Path.GetFileNameWithoutExtension(path);
            var side = int.Parse(file.AsSpan(6, file.IndexOf('x') - 6));
            var payload = Payloads.GetOrAdd(side, static size => new Lazy<byte[]>(() => new byte[size * size])).Value;
            content = new StreamContent(new ThrottledSource(payload, Download));
        }
        return new(HttpStatusCode.OK) { Content = content, RequestMessage = request };
    }

    private sealed class ThrottledSource(byte[] data, Throttle throttle) : MemoryStream(data, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var length = (int)Math.Min(buffer.Length, Length - Position);
            if (length == 0) return 0;
            await throttle.TransferAsync(length, token);
            return base.Read(buffer.Span[..length]);
        }
    }

    private sealed class ThrottledSink(Throttle throttle, CancellationToken stopping) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            await throttle.TransferAsync(buffer.Length, stopping);
        }
    }
}
