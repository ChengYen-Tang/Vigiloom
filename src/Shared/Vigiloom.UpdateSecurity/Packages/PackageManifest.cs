using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using NuGet.Versioning;

namespace Vigiloom.UpdateSecurity;

/// <summary>Publisher-authenticated details for one package, independent of central assignment.</summary>
public sealed class PackageManifest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    private readonly NuGetVersion minimumUpdater;
    private readonly NuGetVersion? minimumAgent;
    private readonly NuGetVersion? maximumAgent;

    private PackageManifest(Document value, NuGetVersion minimumUpdater, NuGetVersion? minimumAgent,
        NuGetVersion? maximumAgent, DateTimeOffset expires, string manifestSha256)
    {
        Version = value.Version!; RuntimeIdentifier = value.Rid!; Size = value.Size; Sha256 = value.Sha256!;
        this.minimumUpdater = minimumUpdater; this.minimumAgent = minimumAgent; this.maximumAgent = maximumAgent;
        Expires = expires;
        ManifestSha256 = manifestSha256;
    }

    public string Version { get; }
    public string RuntimeIdentifier { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public DateTimeOffset Expires { get; }
    public string ManifestSha256 { get; }

    public static PackageManifest Parse(VerifiedManifest verified, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (verified.Expires <= now) throw new InvalidDataException("The package manifest has expired.");
        try
        {
            var value = JsonSerializer.Deserialize<Document>(verified.Content.Span, Options)
                ?? throw new InvalidDataException("The package manifest is empty.");
            if (value.Schema != 1 || !PackageRules.TryVersion(value.Version, out _) || !PackageRules.IsRuntime(value.Rid) ||
                value.Size is <= 0 or > PackageRules.MaximumPackageBytes || !PackageRules.IsHash(value.Sha256) ||
                !PackageRules.TryVersion(value.MinimumUpdaterVersion, out var minimumUpdater))
                throw new InvalidDataException("The package manifest contains invalid package metadata.");
            NuGetVersion? minimum = null, maximum = null;
            if (value.MinimumAgentVersion is not null && !PackageRules.TryVersion(value.MinimumAgentVersion, out minimum!))
                throw new InvalidDataException("The minimum Agent version is invalid.");
            if (value.MaximumAgentVersion is not null && !PackageRules.TryVersion(value.MaximumAgentVersion, out maximum!))
                throw new InvalidDataException("The maximum Agent version is invalid.");
            if (minimum is not null && maximum is not null && minimum > maximum)
                throw new InvalidDataException("The Agent compatibility range is invalid.");
            var digest = Convert.ToHexStringLower(SHA256.HashData(verified.Content.Span));
            return new PackageManifest(value, minimumUpdater, minimum, maximum, verified.Expires, digest);
        }
        catch (JsonException error) { throw new InvalidDataException("The package manifest is malformed.", error); }
    }

    /// <summary>Checks that the individual manifest and the central signed catalogue describe the same release.</summary>
    public bool Matches(PackageEntry entry) => entry is not null && Version == entry.Version && RuntimeIdentifier == entry.Rid &&
        Size == entry.Size && Sha256 == entry.Sha256 && ManifestSha256 == entry.ManifestSha256;

    /// <remarks>This does not establish assignment or revocation authorization.</remarks>
    public bool IsCompatible(string runtimeIdentifier, string updaterVersion, string? currentAgentVersion, DateTimeOffset now)
    {
        if (Expires <= now || RuntimeIdentifier != runtimeIdentifier || !PackageRules.TryVersion(updaterVersion, out var updater) || updater < minimumUpdater)
            return false;
        if (currentAgentVersion is null) return minimumAgent is null && maximumAgent is null;
        return PackageRules.TryVersion(currentAgentVersion, out var current) &&
            (minimumAgent is null || current >= minimumAgent) && (maximumAgent is null || current <= maximumAgent);
    }

    private sealed record Document(int Schema, string? Version, string? Rid, long Size, string? Sha256,
        string? MinimumUpdaterVersion, string? MinimumAgentVersion, string? MaximumAgentVersion);
}
