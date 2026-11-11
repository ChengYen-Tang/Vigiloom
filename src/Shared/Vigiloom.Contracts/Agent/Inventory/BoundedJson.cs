using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>Bounds the buffer used by the serializer, not just the final destination.</summary>
internal static class BoundedJson
{
    internal static byte[] Serialize<T>(T value, JsonSerializerOptions options)
    {
        using var buffer = new LimitedBuffer(AgentBatchCodec.MaximumBytes);
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, value, options);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static int Measure<T>(T value, JsonSerializerOptions options)
    {
        using var buffer = new LimitedBuffer(AgentBatchCodec.MaximumBytes);
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, value, options);
        writer.Flush();
        return buffer.WrittenSpan.Length;
    }

    internal static (int Bytes, byte[] Digest) MeasureHash<T>(T value, JsonSerializerOptions options)
    {
        using var buffer = new LimitedBuffer(AgentBatchCodec.MaximumBytes);
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, value, options);
        writer.Flush();
        return (buffer.WrittenSpan.Length, SHA256.HashData(buffer.WrittenSpan));
    }

    private sealed class LimitedBuffer(int limit) : IBufferWriter<byte>, IDisposable
    {
        private byte[]? array;
        private int written;
        public ReadOnlySpan<byte> WrittenSpan => array.AsSpan(0, written);

        public void Advance(int count)
        {
            if (count < 0 || count > Math.Min(limit - written, (array?.Length ?? 0) - written))
                throw new ArgumentException("Inventory JSON exceeds its encoded size limit.");
            written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return array.AsMemory(written, Math.Min(limit - written, array!.Length - written));
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return array.AsSpan(written, Math.Min(limit - written, array!.Length - written));
        }

        private void Ensure(int sizeHint)
        {
            var remaining = limit - written;
            if (sizeHint < 0 || Math.Max(1, sizeHint) > remaining) throw new ArgumentException("Inventory JSON exceeds its encoded size limit.");
            var required = written + Math.Max(1, sizeHint);
            if (array is not null && array.Length >= required) return;
            var next = ArrayPool<byte>.Shared.Rent(Math.Min(limit, Math.Max(required, array is null ? 4096 : array.Length * 2)));
            if (array is not null)
            {
                array.AsSpan(0, written).CopyTo(next);
                ArrayPool<byte>.Shared.Return(array, clearArray: true);
            }
            array = next;
        }

        public void Dispose()
        {
            if (array is not null) ArrayPool<byte>.Shared.Return(array, clearArray: true);
            array = null;
            written = 0;
        }
    }
}
