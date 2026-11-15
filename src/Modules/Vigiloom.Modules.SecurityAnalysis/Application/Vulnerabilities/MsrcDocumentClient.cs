using System.Net;
using System.Net.Http.Headers;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Fetches one explicit monthly CVRF document from Microsoft's fixed API endpoint.</summary>
internal sealed class MsrcDocumentClient(HttpClient client)
{
    internal async Task<IReadOnlyList<MsrcCatalogEntry>> GetCatalogAsync(int maximumBytes, int maximumEntries, CancellationToken token)
    {
        if (maximumBytes <= 0 || maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var content = await GetAsync(new Uri("https://api.msrc.microsoft.com/cvrf/v3.0/updates"), maximumBytes, token);
        token.ThrowIfCancellationRequested();
        var catalog = MsrcCatalogReader.Read(content, maximumBytes, maximumEntries);
        token.ThrowIfCancellationRequested();
        return catalog;
    }

    internal async Task<(byte[] Artifact, MsrcDocument Document)> FetchAsync(string id, int maximumBytes, int maximumRecords, CancellationToken token)
    {
        if (!MsrcCatalogReader.ValidId(id) || maximumBytes <= 0 || maximumRecords <= 0)
            throw new ArgumentException("A valid MSRC document identifier and positive bounds are required.");
        var artifact = await GetAsync(new Uri($"https://api.msrc.microsoft.com/cvrf/v3.0/cvrf/{id}"), maximumBytes, token);
        token.ThrowIfCancellationRequested();
        MsrcDocument document;
        try { document = MsrcCvrfReader.Read(artifact, maximumBytes, maximumRecords); }
        catch (InvalidDataException error) { throw new InvalidDataException($"Invalid MSRC CVRF document {id}.", error); }
        token.ThrowIfCancellationRequested();
        if (!string.Equals(document.Id, id, StringComparison.Ordinal)) throw new InvalidDataException("MSRC response does not match the requested month.");
        return (artifact, document);
    }

    private async Task<byte[]> GetAsync(Uri endpoint, int maximumBytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unexpected MSRC response.");
        return await BoundedHttpBody.ReadAsync(response.Content, maximumBytes, timeout.Token);
    }
}
