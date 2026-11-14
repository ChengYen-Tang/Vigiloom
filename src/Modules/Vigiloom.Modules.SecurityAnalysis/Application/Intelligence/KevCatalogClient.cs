using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Retrieves a bounded snapshot from the official KEV feed.</summary>
internal sealed class KevCatalogClient(HttpClient client)
{
    private static readonly Uri Feed = new("https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json");

    internal async Task<KevDownload> FetchAsync(string? etag, DateTimeOffset? modifiedAt, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        using var request = new HttpRequestMessage(HttpMethod.Get, Feed);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (etag is not null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        if (modifiedAt is not null) request.Headers.IfModifiedSince = modifiedAt;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (etag is null && modifiedAt is null) throw new InvalidDataException("Unexpected unconditional KEV not-modified response.");
            return new(null, null, response.Headers.ETag?.ToString() ?? etag, response.Content.Headers.LastModified ?? modifiedAt);
        }
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("Unexpected KEV response status.");
        if (response.Content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unsupported KEV content encoding.");
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidDataException("KEV response exceeds the allowed size.");

        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)) != 0)
            {
                if (output.Length + read > maxBytes) throw new InvalidDataException("KEV response exceeds the allowed size.");
                output.Write(buffer, 0, read);
            }
            var bytes = output.ToArray();
            return new(bytes, SHA256.HashData(bytes), response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed record KevDownload(byte[]? Content, byte[]? Digest, string? ETag, DateTimeOffset? LastModified);
