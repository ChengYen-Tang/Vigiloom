using System.Buffers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Infrastructure.AgentPackages;

/// <summary>Reads Agent releases and their asset hashes from the public Vigiloom repository.</summary>
public sealed class GitHubAgentReleases(HttpClient client)
{
    private const string Repository = "repos/ChengYen-Tang/Vigiloom";
    private const int PageSize = 100;

    public async Task<AgentReleasePage> GetAsync(int page, CancellationToken cancellationToken)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var releases = await client.GetFromJsonAsync<Release[]>(
            $"{Repository}/releases?per_page={PageSize}&page={page}", cancellationToken) ?? [];
        var items = releases.Select(ToAgentRelease).OfType<AgentRelease>().ToArray();
        return new(items, page, releases.Length == PageSize);
    }

    public async Task<AgentReleaseAsset?> GetAssetAsync(string version, string runtimeIdentifier, CancellationToken cancellationToken)
    {
        if (!PackageRules.TryVersion(version, out _) || !PackageRules.IsRuntime(runtimeIdentifier)) return null;
        var release = await client.GetFromJsonAsync<Release>(
            $"{Repository}/releases/tags/{Uri.EscapeDataString("agent-v" + version)}", cancellationToken);
        return ToAgentRelease(release)?.Assets.FirstOrDefault(asset => asset.RuntimeIdentifier == runtimeIdentifier);
    }

    public async Task DownloadAsync(string version, AgentReleaseAsset asset, string destination, CancellationToken cancellationToken)
    {
        var tag = Uri.EscapeDataString("agent-v" + version);
        var url = $"https://github.com/ChengYen-Tang/Vigiloom/releases/download/{tag}/agent-{asset.RuntimeIdentifier}.zip";
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long length = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) break;
                length += count;
                if (length > asset.Size) throw new InvalidDataException("Downloaded package exceeds its GitHub size.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (length != asset.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(asset.Sha256)))
                throw new InvalidDataException("Downloaded package does not match GitHub's SHA-256.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static AgentRelease? ToAgentRelease(Release? release)
    {
        if (release is null || release.Draft || release.TagName is not { Length: > 7 } tag ||
            !tag.StartsWith("agent-v", StringComparison.Ordinal) || !PackageRules.TryVersion(tag[7..], out _)) return null;
        var assets = (release.Assets ?? []).Select(ToAgentAsset).OfType<AgentReleaseAsset>().ToArray();
        return new(tag[7..], release.Prerelease, release.PublishedAt, assets);
    }

    private static AgentReleaseAsset? ToAgentAsset(Asset asset)
    {
        var rid = asset.Name switch
        {
            "agent-win-x64.zip" => "win-x64",
            "agent-win-x86.zip" => "win-x86",
            "agent-linux-x64.zip" => "linux-x64",
            _ => null
        };
        var digest = asset.Digest is { } value && value.StartsWith("sha256:", StringComparison.Ordinal) ? value[7..] : null;
        return rid is not null && asset.Size > 0 && PackageRules.IsHash(digest)
            ? new(rid, asset.Size, digest!) : null;
    }

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("assets")] Asset[]? Assets);
    private sealed record Asset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);
}

public sealed record AgentReleaseAsset(string RuntimeIdentifier, long Size, string Sha256);
public sealed record AgentRelease(string Version, bool Preview, DateTimeOffset? PublishedAt, IReadOnlyList<AgentReleaseAsset> Assets);
public sealed record AgentReleasePage(IReadOnlyList<AgentRelease> Items, int Page, bool HasMore);
