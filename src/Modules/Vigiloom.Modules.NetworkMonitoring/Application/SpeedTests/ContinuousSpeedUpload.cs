using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using NetPace.Core;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

// Each lane sends its next request as soon as its preceding response succeeds.
// A lane never claims work from another lane or a shared task queue.
internal static class ContinuousSpeedUpload
{
    private const int InitialBytes = 100_000;
    private const int MinimumBytes = 50_000;
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int SampleMilliseconds = 500;

    internal static async Task<(long Bytes, long Milliseconds, int Failures, bool TimedOut)> RunAsync(
        HttpClient client, IServer server, SpeedTrafficBudget budget, int maxMiB, int targetMilliseconds,
        double phaseDeadlineSeconds, int maxConnections, Action<double?> update, CancellationToken deadline)
    {
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        phase.CancelAfter(TimeSpan.FromSeconds(phaseDeadlineSeconds));
        Uri endpoint;
        try { endpoint = await ResolveEndpointAsync(client, new Uri(server.Url), phase.Token); }
        catch (OperationCanceledException) when (phase.IsCancellationRequested && !deadline.IsCancellationRequested)
        {
            return (0, 0, 1, true);
        }

        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(phase.Token);
        transfer.CancelAfter(TimeSpan.FromMilliseconds(targetMilliseconds));
        var clock = Stopwatch.StartNew();
        var before = budget.BytesSent;
        var phaseLimit = Math.Max(1, maxMiB / 2) * 1024L * 1024L;
        var gate = new object();
        long assigned = 0, confirmed = 0;
        int failed = 0, active = 0;
        var samples = new List<(long Bytes, long Milliseconds)>();

        async Task LaneAsync(int index)
        {
            try
            {
                // Start four connections; add lanes during ramp-up when measured
                // throughput warrants them, without exceeding the configured limit.
                if (index >= 4)
                {
                    var allowed = 4;
                    while (clock.ElapsedMilliseconds < targetMilliseconds / 2 && index >= allowed)
                    {
                        await Task.Delay(500, transfer.Token);
                        allowed = SpeedMeasurementMath.NextDownloadConcurrency(4, 1,
                            Interlocked.Read(ref confirmed), clock.ElapsedMilliseconds);
                    }
                    if (index >= allowed) return;
                }
                Interlocked.Increment(ref active);
                try
                {
                    while (!transfer.IsCancellationRequested)
                    {
                        int size;
                        lock (gate)
                        {
                            var remaining = phaseLimit - assigned;
                            if (remaining <= 0) break;
                            var elapsed = Math.Max(1, clock.ElapsedMilliseconds);
                            var estimate = confirmed == 0 ? InitialBytes :
                                Math.Clamp((double)confirmed * 1000 / elapsed / Math.Max(1, Volatile.Read(ref active)), MinimumBytes, MaximumBytes);
                            size = (int)Math.Min(remaining, estimate);
                            assigned += size;
                        }
                        try
                        {
                            using var content = new UploadContent(size);
                            using var response = await client.PostAsync(endpoint, content, transfer.Token);
                            response.EnsureSuccessStatusCode();
                            Interlocked.Add(ref confirmed, size);
                        }
                        catch (OperationCanceledException) when (transfer.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                        {
                            Interlocked.Increment(ref failed);
                            break;
                        }
                    }
                }
                finally { Interlocked.Decrement(ref active); }
            }
            catch (OperationCanceledException) when (transfer.IsCancellationRequested) { }
        }

        var workers = Task.WhenAll(Enumerable.Range(0, Math.Max(1, maxConnections)).Select(LaneAsync));
        long lastTime = 0, lastBytes = 0;
        var stoppedAtTarget = false;
        try
        {
            while (!workers.IsCompleted && !transfer.IsCancellationRequested)
            {
                try { await Task.WhenAny(workers, Task.Delay(SampleMilliseconds, transfer.Token)); }
                catch (OperationCanceledException) when (transfer.IsCancellationRequested) { }
                if (transfer.IsCancellationRequested) break;
                RecordSample();
            }
        }
        finally
        {
            stoppedAtTarget = transfer.IsCancellationRequested && !phase.IsCancellationRequested;
            transfer.Cancel();
            await workers;
        }
        RecordSample();
        deadline.ThrowIfCancellationRequested();
        var elapsedMilliseconds = stoppedAtTarget ? targetMilliseconds : Math.Min(clock.ElapsedMilliseconds, targetMilliseconds);
        var timedOut = phase.IsCancellationRequested && !deadline.IsCancellationRequested;
        return (Math.Max(0, budget.BytesSent - before), elapsedMilliseconds, Volatile.Read(ref failed), timedOut);

        void RecordSample()
        {
            var now = Math.Min(clock.ElapsedMilliseconds, targetMilliseconds);
            var current = Interlocked.Read(ref confirmed);
            var duration = now - lastTime;
            var bytes = current - lastBytes;
            if (duration <= 0) return;
            lastTime = now;
            lastBytes = current;
            samples.Add((bytes, duration));
            if (samples.Any(sample => sample.Bytes > 0)) update(SpeedMeasurementMath.CalculateTrimmedRate(samples));
        }
    }

    private static async Task<Uri> ResolveEndpointAsync(HttpClient client, Uri endpoint, CancellationToken token)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            using var content = new ByteArrayContent([]);
            using var response = await client.PostAsync(endpoint, content, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                endpoint = new Uri(endpoint, location);
                continue;
            }
            // Some legacy endpoints reject an empty probe while accepting upload bodies.
            // Let the measured request establish whether the endpoint actually works.
            return endpoint;
        }
        throw new HttpRequestException("Upload endpoint exceeded redirect limit.");
    }

    private sealed class UploadContent(int length) : HttpContent
    {
        private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(64 * 1024);
        protected override bool TryComputeLength(out long size) { size = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            var remaining = length;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, Payload.Length);
                await stream.WriteAsync(Payload.AsMemory(0, count), token);
                remaining -= count;
            }
        }
    }
}
