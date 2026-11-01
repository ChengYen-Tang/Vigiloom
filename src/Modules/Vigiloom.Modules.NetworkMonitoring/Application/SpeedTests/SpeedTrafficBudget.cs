using System.Net;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

internal enum TrafficDirection { Received, Sent }

// Shared by all concurrent request and response bodies in one measurement.
internal sealed class SpeedTrafficBudget
{
    private readonly object gate = new();
    private readonly long maximum;
    private readonly Action onLimit;
    private readonly CancellationToken stopping;
    private long remaining;
    private long transferred;
    private long received;
    private long sent;
    private bool reached;
    private TaskCompletionSource? changed;

    public SpeedTrafficBudget(long maximum, Action onLimit, CancellationToken stopping)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        this.maximum = maximum; this.onLimit = onLimit; this.stopping = stopping; remaining = maximum;
    }

    public long BytesTransferred { get { lock (gate) return transferred; } }
    public long BytesReceived { get { lock (gate) return received; } }
    public long BytesSent { get { lock (gate) return sent; } }
    public bool Reached { get { lock (gate) return reached; } }

    public int Reserve(int requested)
    {
        var reservation = ReserveAsync(requested, stopping);
        return reservation.IsCompletedSuccessfully ? reservation.Result : reservation.AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask<int> ReserveAsync(int requested, CancellationToken cancellationToken)
    {
        if (requested == 0) return 0;
        while (true)
        {
            stopping.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (gate)
            {
                if (reached) throw new IOException("Speed-test traffic budget exhausted.");
                if (remaining > 0)
                {
                    var allowed = (int)Math.Min(requested, remaining);
                    remaining -= allowed;
                    return allowed;
                }
                // Other I/O owns the remaining budget but may use less than it reserved.
                // Wait for its actual byte count instead of treating reservations as traffic.
                changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = changed.Task;
            }
            await wait.WaitAsync(stopping).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Complete(int reserved, int actual, TrafficDirection direction = TrafficDirection.Received)
    {
        var exhausted = false;
        TaskCompletionSource? notify;
        lock (gate)
        {
            remaining += reserved - actual;
            transferred += actual;
            if (direction == TrafficDirection.Sent) sent += actual;
            else received += actual;
            if (transferred >= maximum && !reached) { reached = true; exhausted = true; }
            notify = changed;
            changed = null;
        }
        try { if (exhausted) onLimit(); }
        finally { notify?.TrySetResult(); }
    }
}

internal sealed class SpeedBudgetContent : HttpContent
{
    private readonly HttpContent inner;
    private readonly SpeedTrafficBudget budget;
    private readonly TrafficDirection direction;

    public SpeedBudgetContent(HttpContent inner, SpeedTrafficBudget budget, TrafficDirection direction)
    {
        this.inner = inner;
        this.budget = budget;
        this.direction = direction;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var limited = new SpeedBudgetStream(stream, budget, direction, leaveOpen: true);
        await inner.CopyToAsync(limited, cancellationToken);
    }
    protected override async Task<Stream> CreateContentReadStreamAsync() => new SpeedBudgetStream(await inner.ReadAsStreamAsync(), budget, direction, leaveOpen: false);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        new SpeedBudgetStream(await inner.ReadAsStreamAsync(cancellationToken), budget, direction, leaveOpen: false);
    protected override bool TryComputeLength(out long length)
    {
        length = inner.Headers.ContentLength.GetValueOrDefault();
        return inner.Headers.ContentLength.HasValue;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class SpeedBudgetStream(Stream inner, SpeedTrafficBudget budget, TrafficDirection direction, bool leaveOpen) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var reserved = budget.Reserve(count);
        var actual = 0;
        try { return actual = inner.Read(buffer, offset, reserved); }
        finally { budget.Complete(reserved, actual, direction); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        var reserved = await budget.ReserveAsync(buffer.Length, token).ConfigureAwait(false);
        var actual = 0;
        try { return actual = await inner.ReadAsync(buffer[..reserved], token); }
        finally { budget.Complete(reserved, actual, direction); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            var reserved = budget.Reserve(count);
            var actual = 0;
            try { inner.Write(buffer, offset, reserved); actual = reserved; }
            finally { budget.Complete(reserved, actual, direction); }
            offset += reserved;
            count -= reserved;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        while (!buffer.IsEmpty)
        {
            var reserved = await budget.ReserveAsync(buffer.Length, token).ConfigureAwait(false);
            var actual = 0;
            try { await inner.WriteAsync(buffer[..reserved], token).ConfigureAwait(false); actual = reserved; }
            finally { budget.Complete(reserved, actual, direction); }
            buffer = buffer[reserved..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
}
