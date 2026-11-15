using System.Buffers.Binary;
using System.IO.Hashing;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Limits expanded input as well as the separately limited compressed HTTP body.</summary>
internal sealed class EpssLimitedStream(Stream inner, long limit) : Stream
{
    private long read;
    private readonly Crc32 checksum = new();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) { var count = inner.Read(buffer); Count(buffer[..count]); return count; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        CountMemory(buffer, await inner.ReadAsync(buffer, cancellationToken));
    private int CountMemory(Memory<byte> buffer, int count) { Count(buffer.Span[..count]); return count; }
    private void Count(ReadOnlySpan<byte> buffer)
    {
        read += buffer.Length;
        if (read > limit) throw new InvalidDataException("Expanded EPSS snapshot exceeds the allowed size.");
        checksum.Append(buffer);
    }
    internal void ValidateFooter(ReadOnlySpan<byte> artifact)
    {
        if (artifact.Length < 18 || !artifact.StartsWith(new byte[] { 0x1f, 0x8b, 0x08 }) ||
            BinaryPrimitives.ReadUInt32LittleEndian(artifact[^8..]) != checksum.GetCurrentHashAsUInt32() ||
            BinaryPrimitives.ReadUInt32LittleEndian(artifact[^4..]) != (uint)read)
            throw new InvalidDataException("EPSS gzip trailer does not match its expanded data.");
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
