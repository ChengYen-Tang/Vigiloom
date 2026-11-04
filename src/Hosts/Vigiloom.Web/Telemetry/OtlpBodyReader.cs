using System.Buffers;
using System.IO.Compression;
using Google.Protobuf;

namespace Vigiloom.Web.Telemetry;

internal enum OtlpBodyStatus { Success, Invalid, TooLarge, UnsupportedEncoding }
internal sealed record OtlpBody<T>(OtlpBodyStatus Status, T? Message) where T : class;

/// <summary>Reads the entire wire body and bounds it and its decompressed form independently.</summary>
internal static class OtlpBodyReader
{
    internal const int MaximumBytes = 4 * 1024 * 1024;

    internal static async Task<OtlpBody<T>> ReadAsync<T>(HttpRequest request, MessageParser<T> parser, CancellationToken token)
        where T : class, IMessage<T>
    {
        var encodings = request.Headers.ContentEncoding;
        var gzip = encodings.Count == 1 && string.Equals(encodings[0], "gzip", StringComparison.OrdinalIgnoreCase);
        if (encodings.Count != 0 && !gzip) return new(OtlpBodyStatus.UnsupportedEncoding, null);
        if (request.ContentLength > MaximumBytes) return new(OtlpBodyStatus.TooLarge, null);

        var wire = ArrayPool<byte>.Shared.Rent(MaximumBytes);
        byte[]? decoded = null;
        try
        {
            int wireLength;
            try { wireLength = await ReadBoundedAsync(request.Body, wire, token); }
            catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
            { return new(OtlpBodyStatus.TooLarge, null); }
            if (wireLength > MaximumBytes) return new(OtlpBodyStatus.TooLarge, null);

            var buffer = wire;
            var length = wireLength;
            if (gzip)
            {
                decoded = ArrayPool<byte>.Shared.Rent(MaximumBytes);
                using var encoded = new MemoryStream(wire, 0, wireLength, writable: false);
                using var decompressor = new GZipStream(encoded, CompressionMode.Decompress);
                try { length = await ReadBoundedAsync(decompressor, decoded, token); }
                catch (InvalidDataException) { return new(OtlpBodyStatus.Invalid, null); }
                if (length > MaximumBytes) return new(OtlpBodyStatus.TooLarge, null);
                buffer = decoded;
            }
            // The size limit alone does not bound the number of objects the parser can allocate.
            if (!ProtobufBudget.Allows(buffer.AsSpan(0, length), parser.ParseFrom(Array.Empty<byte>()).Descriptor))
                return new(OtlpBodyStatus.Invalid, null);
            try { return new(OtlpBodyStatus.Success, parser.ParseFrom(buffer, 0, length)); }
            catch (InvalidProtocolBufferException) { return new(OtlpBodyStatus.Invalid, null); }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(wire, clearArray: true);
            if (decoded is not null) ArrayPool<byte>.Shared.Return(decoded, clearArray: true);
        }
    }

    private static async Task<int> ReadBoundedAsync(Stream input, byte[] buffer, CancellationToken token)
    {
        var length = 0;
        while (length < MaximumBytes)
        {
            var read = await input.ReadAsync(buffer.AsMemory(length, MaximumBytes - length), token);
            if (read == 0) return length;
            length += read;
        }
        var extra = new byte[1];
        return await input.ReadAsync(extra, token) == 0 ? length : MaximumBytes + 1;
    }
}
