using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.Web.AgentPackages;

/// <summary>Lists Agent releases from the public Vigiloom GitHub repository.</summary>
public sealed class GitHubAgentReleases(HttpClient client)
{
    private const int PageSize = 100;

    public async Task<AgentReleasePage> GetAsync(int page, CancellationToken cancellationToken)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var releases = await client.GetFromJsonAsync<Release[]>(
            $"repos/ChengYen-Tang/Vigiloom/releases?per_page={PageSize}&page={page}", cancellationToken) ?? [];
        var items = releases.Where(x => !x.Draft && x.TagName is { Length: > 7 } && x.TagName.StartsWith("agent-v", StringComparison.Ordinal))
            .Select(x => new AgentRelease(x.TagName![7..], x.Prerelease, x.PublishedAt)).ToArray();
        return new(items, page, releases.Length == PageSize);
    }

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt);
}

public sealed record AgentRelease(string Version, bool Preview, DateTimeOffset? PublishedAt);
public sealed record AgentReleasePage(IReadOnlyList<AgentRelease> Items, int Page, bool HasMore);
