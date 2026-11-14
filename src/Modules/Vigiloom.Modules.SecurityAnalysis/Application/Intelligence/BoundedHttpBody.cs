using System.Buffers;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Reads an identity-encoded response without exceeding its configured byte limit.</summary>
internal static class BoundedHttpBody
{
    internal static async Task<byte[]> ReadAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unsupported response content encoding.");
        if (content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidDataException("Response exceeds the allowed size.");
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) != 0)
            {
                if (output.Length + read > maxBytes) throw new InvalidDataException("Response exceeds the allowed size.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
