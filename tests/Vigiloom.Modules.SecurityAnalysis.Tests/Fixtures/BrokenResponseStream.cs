namespace Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

internal sealed class BrokenResponseStream : MemoryStream
{
    private bool read;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (read) throw new HttpIOException(HttpRequestError.ResponseEnded, "The upstream response ended prematurely.");
        read = true;
        buffer.Span[0] = (byte)'[';
        return ValueTask.FromResult(1);
    }
}
