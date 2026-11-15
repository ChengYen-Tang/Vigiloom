using System.Net;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Downloads a fixed upstream endpoint without automatic content transformation.</summary>
internal sealed class VendorSnapshotClient(HttpClient client, Uri endpoint, bool compressed = false, TimeSpan? downloadTimeout = null)
{
    internal async Task<byte[]> FetchAsync(int maximumBytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.AcceptEncoding.ParseAdd(compressed ? "gzip" : "identity");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(downloadTimeout ?? TimeSpan.FromMinutes(3));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unexpected vendor snapshot response.");
        if (response.Content.Headers.ContentLength is not long length)
            return await BoundedHttpBody.ReadAsync(response.Content, maximumBytes, timeout.Token);
        if (length <= 0 || length > maximumBytes) throw new InvalidDataException("Vendor snapshot response exceeds the allowed size.");
        var result = GC.AllocateUninitializedArray<byte>(checked((int)length));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        await stream.ReadExactlyAsync(result, timeout.Token);
        if (await stream.ReadAsync(new byte[1], timeout.Token) != 0) throw new InvalidDataException("Vendor snapshot response exceeds its declared size.");
        return result;
    }
}
