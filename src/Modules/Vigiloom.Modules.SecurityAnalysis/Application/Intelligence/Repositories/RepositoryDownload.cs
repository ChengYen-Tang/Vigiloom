using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;

internal enum RepositorySource { Sigma, SignatureBase }

/// <summary>Fetches only compiled-in repositories; no upstream URL or credential is forwarded.</summary>
internal sealed class RepositoryDownload(HttpClient client, RepositorySource source)
{
    internal async Task<string> ResolveCommitAsync(CancellationToken token)
    {
        using var request = Request(source switch
        {
            RepositorySource.Sigma => "https://api.github.com/repos/SigmaHQ/sigma/commits/master",
            RepositorySource.SignatureBase => "https://api.github.com/repos/Neo23x0/signature-base/commits/master",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new InvalidDataException("Unexpected Repository commit response.");
        var body = await BoundedHttpBody.ReadAsync(response.Content, 1024 * 1024, token);
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
            var commit = json.RootElement.GetProperty("sha").GetString();
            if (commit is null || commit.Length != 40 || commit.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new InvalidDataException("Invalid Repository commit identifier.");
            return commit;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Invalid Repository commit response.", ex); }
    }

    internal async Task<byte[]> SaveArchiveAsync(string commit, Stream output, int maximumBytes, CancellationToken token)
    {
        if (commit.Length != 40 || commit.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid Repository commit.", nameof(commit));
        using var request = Request(source switch
        {
            RepositorySource.Sigma => $"https://codeload.github.com/SigmaHQ/sigma/tar.gz/{commit}",
            RepositorySource.SignatureBase => $"https://codeload.github.com/Neo23x0/signature-base/tar.gz/{commit}",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength > maximumBytes ||
            response.Content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Invalid Repository archive response.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) != 0)
            {
                total += read;
                if (total > maximumBytes) throw new InvalidDataException("Repository archive exceeds the configured limit.");
                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (total == 0) throw new InvalidDataException("Repository archive is empty.");
            return hash.GetHashAndReset();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static HttpRequestMessage Request(string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("Vigiloom/1.0");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }
}
