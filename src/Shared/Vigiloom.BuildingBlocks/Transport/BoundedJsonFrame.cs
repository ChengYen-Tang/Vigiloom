using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.BuildingBlocks.Transport;

/// <summary>Reads and writes a single length-prefixed JSON message on a local stream.</summary>
public static class BoundedJsonFrame
{
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        try
        {
            if (payload.Length is 0 or > MaximumBytes) throw new InvalidDataException("The local message is too large.");
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(payload, token);
            await stream.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumBytes) throw new InvalidDataException("The local message has an invalid length.");
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), token);
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), Options)
                ?? throw new InvalidDataException("The local message is empty.");
        }
        finally { CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length)); ArrayPool<byte>.Shared.Return(buffer); }
    }
}
