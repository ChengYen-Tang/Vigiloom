namespace Vigiloom.Testing.SpeedTests;

internal sealed class ControlledPhaseFactory(ControlledPhaseHandler handler) : IHttpMessageHandlerFactory
{
    public HttpMessageHandler CreateHandler(string name) => handler;
}

internal sealed class ControlledPhaseHandler(int downloadDelayMs = 200, int uploadDelayMs = 100, int probeDelayMs = 0,
    bool slowUploads = false, int maximumDownloadSide = 4000,
    System.Net.HttpStatusCode unavailableStatus = System.Net.HttpStatusCode.NotFound, bool mixedUnavailableStatus = false,
    bool throwUnavailable = false) : HttpMessageHandler
{
    private static readonly byte[] Download = new byte[2 * 1048576];
    private static readonly byte[] UploadResponse = new byte[131072];
    private readonly object gate = new();
    private int uploadRequests;
    private int uploadProbes;
    private int uploadPayloadsCompleted;
    private int unavailableRequests;
    private long uploadBytes, firstUpload, lastUpload, completedUploadTicks;
    private long slowUploadBytes;
    private long downloadBytes;
    public int UploadRequests => Volatile.Read(ref uploadRequests);
    public int UploadProbes => Volatile.Read(ref uploadProbes);
    public int UploadPayloadsCompleted => Volatile.Read(ref uploadPayloadsCompleted);
    public System.Collections.Concurrent.ConcurrentQueue<long> UploadPayloadSizes { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<string> DownloadPaths { get; } = new();
    public TaskCompletionSource UnsupportedRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long SlowUploadBytes => Interlocked.Read(ref slowUploadBytes);
    public long DownloadBytes => Interlocked.Read(ref downloadBytes);
    public long UploadBytes { get { lock (gate) return uploadBytes; } }
    public double UploadElapsedSeconds
    {
        get
        {
            lock (gate)
            {
                var current = firstUpload == 0 ? 0 : lastUpload - firstUpload;
                return (completedUploadTicks + current) / (double)System.Diagnostics.Stopwatch.Frequency;
            }
        }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/js/config-sdk") return new(System.Net.HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
        HttpContent content;
        if (path.EndsWith("speedtest-servers-static.php", StringComparison.Ordinal))
            content = new StringContent("<settings><servers><server url=\"http://speedtest.example.com:8080/speedtest/upload.php\" lat=\"25\" lon=\"121\" name=\"Taipei\" country=\"Taiwan\" sponsor=\"Example\" id=\"1\" /></servers></settings>");
        else if (path.EndsWith("latency.txt", StringComparison.Ordinal)) content = new StringContent("test=test");
        else if (request.Method == HttpMethod.Post)
        {
            var length = request.Content?.Headers.ContentLength ?? 0;
            if (length > 0) UploadPayloadSizes.Enqueue(length);
            if (length == 0)
            {
                Interlocked.Increment(ref uploadProbes);
                lock (gate)
                {
                    if (firstUpload != 0)
                    {
                        completedUploadTicks += lastUpload - firstUpload;
                        firstUpload = 0;
                        lastUpload = 0;
                    }
                }
                if (probeDelayMs > 0) await Task.Delay(probeDelayMs, token);
            }
            if (length > 0) lock (gate) { if (firstUpload == 0) firstUpload = System.Diagnostics.Stopwatch.GetTimestamp(); }
            if (request.Content is not null)
            {
                if (slowUploads && length > 0)
                {
                    using var sink = new SlowSink(bytes => Interlocked.Add(ref slowUploadBytes, bytes), token);
                    await request.Content.CopyToAsync(sink, token);
                }
                else await request.Content.CopyToAsync(Stream.Null, token);
            }
            Interlocked.Increment(ref uploadRequests);
            // Scale the simulated transfer time with payload size; a fixed delay would
            // make larger uploads unrealistically fast and exhaust the phase budget.
            var payloadUnits = Math.Max(1, (int)Math.Ceiling(length / 1048576d));
            await Task.Delay(uploadDelayMs * payloadUnits, token);
            if (length > 0) Interlocked.Increment(ref uploadPayloadsCompleted);
            if (length > 0) lock (gate) { uploadBytes += length; lastUpload = System.Diagnostics.Stopwatch.GetTimestamp(); }
            content = new ByteArrayContent(UploadResponse);
        }
        else
        {
            DownloadPaths.Enqueue(path);
            var file = Path.GetFileNameWithoutExtension(path);
            var side = int.Parse(file.AsSpan(6, file.IndexOf('x') - 6));
            var unavailable = side > maximumDownloadSide;
            if (unavailable) UnsupportedRequested.TrySetResult();
            await Task.Delay(downloadDelayMs, token);
            if (unavailable)
            {
                if (throwUnavailable) throw new HttpRequestException("Simulated connection failure.");
                var occurrence = Interlocked.Increment(ref unavailableRequests);
                var status = mixedUnavailableStatus && occurrence % 2 == 0 ? System.Net.HttpStatusCode.InternalServerError : unavailableStatus;
                return new HttpResponseMessage(status) { Content = new StringContent("unavailable"), RequestMessage = request };
            }
            content = new StreamContent(new CountingDownload(Download, bytes => Interlocked.Add(ref downloadBytes, bytes)));
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request };
    }

    private sealed class SlowSink(Action<int> received, CancellationToken stopping) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(150, stopping);
            cancellationToken.ThrowIfCancellationRequested();
            received(buffer.Length);
        }
    }

    private sealed class CountingDownload(byte[] payload, Action<int> read) : MemoryStream(payload, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = base.Read(buffer.Span);
            read(count);
            return ValueTask.FromResult(count);
        }
    }
}
