namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Retrieves Debian's public tracker snapshot with a bounded decompressed response.</summary>
internal sealed class DebianTrackerClient(HttpClient client)
{
    private static readonly Uri Feed = new("https://security-tracker.debian.org/tracker/data/json");

    internal Task<byte[]> FetchAsync(int maximumBytes, CancellationToken token) =>
        new VendorSnapshotClient(client, Feed, compressed: true, downloadTimeout: TimeSpan.FromMinutes(15)).FetchAsync(maximumBytes, token);
}
