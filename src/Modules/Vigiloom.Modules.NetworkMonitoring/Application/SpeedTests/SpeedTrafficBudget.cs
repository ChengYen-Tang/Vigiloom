using System.Net;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

// Shared by all concurrent request and response bodies in one measurement.
internal sealed class SpeedTrafficBudget
{
    private readonly object gate = new();
    private readonly long maximum;
    private readonly Action onLimit;
    private long remaining;
    private long transferred;
    private bool reached;

    public SpeedTrafficBudget(long maximum, Action onLimit) { this.maximum = maximum; this.onLimit = onLimit; remaining = maximum; }

    public long BytesTransferred { get { lock (gate) return transferred; } }
    public bool Reached { get { lock (gate) return reached; } }

    public int Reserve(int requested)
    {
        var exhausted = false;
        var allowed = 0;
        lock (gate)
        {
            if (requested == 0) return 0;
            if (remaining == 0) { reached = true; exhausted = true; }
            else { allowed = (int)Math.Min(requested, remaining); remaining -= allowed; }
        }
        if (exhausted) { onLimit(); throw new IOException("Speed-test traffic budget exhausted."); }
        return allowed;
    }

    public void Complete(int reserved, int actual)
    {
        var exhausted = false;
        lock (gate)
        {
            remaining += reserved - actual;
            transferred += actual;
            if (transferred >= maximum && !reached) { reached = true; exhausted = true; }
        }
        if (exhausted) onLimit();
    }
}

internal sealed class SpeedBudgetContent : HttpContent
{
    private readonly HttpContent inner;
    private readonly SpeedTrafficBudget budget;

    public SpeedBudgetContent(HttpContent inner, SpeedTrafficBudget budget)
    {
        this.inner = inner;
        this.budget = budget;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var limited = new SpeedBudgetStream(stream, budget, leaveOpen: true);
        await inner.CopyToAsync(limited, cancellationToken);
    }
    protected override async Task<Stream> CreateContentReadStreamAsync() => new SpeedBudgetStream(await inner.ReadAsStreamAsync(), budget, leaveOpen: false);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        new SpeedBudgetStream(await inner.ReadAsStreamAsync(cancellationToken), budget, leaveOpen: false);
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

internal sealed class SpeedBudgetStream(Stream inner, SpeedTrafficBudget budget, bool leaveOpen) : Stream
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
        finally { budget.Complete(reserved, actual); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        var reserved = budget.Reserve(buffer.Length);
        var actual = 0;
        try { return actual = await inner.ReadAsync(buffer[..reserved], token); }
        finally { budget.Complete(reserved, actual); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        var reserved = budget.Reserve(count);
        var actual = 0;
        try { inner.Write(buffer, offset, reserved); actual = reserved; }
        finally { budget.Complete(reserved, actual); }
        if (reserved < count) throw new IOException("Speed-test traffic budget exhausted.");
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        var reserved = budget.Reserve(buffer.Length);
        var actual = 0;
        try { await inner.WriteAsync(buffer[..reserved], token); actual = reserved; }
        finally { budget.Complete(reserved, actual); }
        if (reserved < buffer.Length) throw new IOException("Speed-test traffic budget exhausted.");
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
}
