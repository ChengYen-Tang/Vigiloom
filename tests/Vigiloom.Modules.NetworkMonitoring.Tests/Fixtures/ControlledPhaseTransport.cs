namespace Vigiloom.Modules.NetworkMonitoring.Tests.Fixtures;

internal sealed class ControlledPhaseFactory(ControlledPhaseHandler handler) : IHttpMessageHandlerFactory
{
    public HttpMessageHandler CreateHandler(string name) => handler;
}

internal sealed class ControlledPhaseHandler : HttpMessageHandler
{
    private static readonly byte[] Download = new byte[2 * 1048576];
    private static readonly byte[] UploadResponse = new byte[131072];
    private readonly object gate = new();
    private int uploadRequests;
    private long uploadBytes, firstUpload, lastUpload;
    public int UploadRequests => Volatile.Read(ref uploadRequests);
    public long UploadBytes { get { lock (gate) return uploadBytes; } }
    public double UploadElapsedSeconds { get { lock (gate) return System.Diagnostics.Stopwatch.GetElapsedTime(firstUpload, lastUpload).TotalSeconds; } }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        HttpContent content;
        if (path.EndsWith("speedtest-servers-static.php", StringComparison.Ordinal))
            content = new StringContent("<settings><servers><server url=\"http://speedtest.example.com:8080/speedtest/upload.php\" lat=\"25\" lon=\"121\" name=\"Taipei\" country=\"Taiwan\" sponsor=\"Example\" id=\"1\" /></servers></settings>");
        else if (path.EndsWith("latency.txt", StringComparison.Ordinal)) content = new StringContent("test=test");
        else if (request.Method == HttpMethod.Post)
        {
            var length = request.Content?.Headers.ContentLength ?? 0;
            if (length > 0) lock (gate) { if (firstUpload == 0) firstUpload = System.Diagnostics.Stopwatch.GetTimestamp(); }
            if (request.Content is not null) await request.Content.CopyToAsync(Stream.Null, token);
            Interlocked.Increment(ref uploadRequests);
            await Task.Delay(100, token);
            if (length > 0) lock (gate) { uploadBytes += length; lastUpload = System.Diagnostics.Stopwatch.GetTimestamp(); }
            content = new ByteArrayContent(UploadResponse);
        }
        else
        {
            await Task.Delay(200, token);
            content = new ByteArrayContent(Download);
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request };
    }
}
