using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Tuf;

namespace Vigiloom.UpdateSecurity;

/// <summary>Downloads only bounded TUF metadata and the signed package catalogue.</summary>
public sealed class BoundedTufRepository : ITufRepository
{
    internal const int MaximumDocumentBytes = 1024 * 1024;
    private const string Catalogue = "catalog.json";
    private readonly HttpClient client;
    private readonly Uri metadataBase;
    private readonly Uri targetsBase;
    private readonly TimeSpan timeout;
    private readonly AuthenticationHeaderValue? authorization;

    public BoundedTufRepository(Uri metadataBase, Uri targetsBase) : this(metadataBase, targetsBase, UpdateHttp.Client, TimeSpan.FromSeconds(30)) { }

    public BoundedTufRepository(Uri metadataBase, Uri targetsBase, string agentCredential)
        : this(metadataBase, targetsBase, UpdateHttp.Client, TimeSpan.FromSeconds(30), agentCredential ?? throw new ArgumentNullException(nameof(agentCredential))) { }

    internal BoundedTufRepository(Uri metadataBase, Uri targetsBase, HttpClient client, TimeSpan? timeout = null, string? agentCredential = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (this.timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.metadataBase = ValidateBase(metadataBase);
        this.targetsBase = ValidateBase(targetsBase);
        if (!string.Equals(this.metadataBase.IdnHost, this.targetsBase.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            this.metadataBase.Port != this.targetsBase.Port)
            throw new ArgumentException("Metadata and catalogue must use the same origin.", nameof(targetsBase));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        if (agentCredential is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(agentCredential);
            authorization = new AuthenticationHeaderValue("Bearer", agentCredential);
        }
    }

    public Task<byte[]?> FetchMetadataAsync(string role, int? version = null, CancellationToken cancellationToken = default)
    {
        if (role is not ("root" or "timestamp" or "snapshot" or "targets") || version is <= 0)
            throw new ArgumentException("An accepted TUF role and positive version are required.");
        var name = version is { } number ? $"{number}.{role}.json" : $"{role}.json";
        return FetchAsync(new Uri(metadataBase, name), cancellationToken);
    }

    public Task<byte[]?> FetchTargetAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        if (!IsPermittedTarget(targetPath)) throw new ArgumentException("Only package catalogues and addressed manifests are supported.", nameof(targetPath));
        return FetchAsync(new Uri(targetsBase, targetPath), cancellationToken);
    }

    private async Task<byte[]?> FetchAsync(Uri uri, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var requestToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = authorization;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDocumentBytes)
            throw new InvalidDataException("TUF document exceeds the configured limit.");
        await using var input = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), requestToken).ConfigureAwait(false);
                if (count == 0) break;
                if (count > MaximumDocumentBytes - output.Length)
                    throw new InvalidDataException("TUF document exceeds the configured limit.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static Uri ValidateBase(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps || value.UserInfo.Length != 0 ||
            value.Query.Length != 0 || value.Fragment.Length != 0 || !value.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("An HTTPS base URI ending in a slash is required.", nameof(value));
        return value;
    }

    private static bool IsPermittedTarget(string? value)
    {
        if (value == Catalogue) return true;
        if (value is null || value.Length < 65 || value[64] != '.' || !IsHash(value.AsSpan(0, 64))) return false;
        var suffix = value.AsSpan(65);
        return suffix.SequenceEqual(Catalogue) || suffix.SequenceEqual("json") ||
            suffix.Length == 69 && suffix[64] == '.' && IsHash(suffix[..64]) && suffix[65..].SequenceEqual("json");
    }

    private static bool IsHash(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }
}
