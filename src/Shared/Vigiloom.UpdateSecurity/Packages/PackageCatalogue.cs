using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.UpdateSecurity;

/// <summary>A validated package list authenticated by a trusted TUF catalogue.</summary>
public sealed class PackageCatalogue
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private PackageCatalogue(IReadOnlyList<PackageEntry> packages, DateTimeOffset expires)
    {
        Packages = packages;
        Expires = expires;
    }

    public IReadOnlyList<PackageEntry> Packages { get; }
    public DateTimeOffset Expires { get; }

    public static PackageCatalogue Parse(VerifiedCatalogue verified, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (verified.Expires <= now) throw new InvalidDataException("The signed catalogue has expired.");
        try
        {
            var document = JsonSerializer.Deserialize<Document>(verified.Content.Span, Options)
                ?? throw new InvalidDataException("The signed catalogue is empty.");
            if (document.Schema != 1 || document.Packages is null || document.Packages.Length > 2000)
                throw new InvalidDataException("Unsupported catalogue schema or package count.");
            var result = new List<PackageEntry>(document.Packages.Length);
            var identities = new HashSet<(string, string)>();
            foreach (var item in document.Packages)
            {
                if (item is null || !PackageRules.TryVersion(item.Version, out var version) ||
                    !PackageRules.IsRuntime(item.Rid) ||
                    item.Size is <= 0 or > PackageRules.MaximumPackageBytes || !PackageRules.IsHash(item.Sha256) || !PackageRules.IsHash(item.ManifestSha256) ||
                    item.Revoked is null ||
                    !identities.Add((version.ToNormalizedString().ToLowerInvariant(), item.Rid!)))
                    throw new InvalidDataException("The signed catalogue contains an invalid or duplicate package.");
                result.Add(new PackageEntry(item.Version!, item.Rid!, item.Size, item.Sha256!, item.ManifestSha256!, item.Revoked.Value));
            }
            return new PackageCatalogue(result.AsReadOnly(), verified.Expires);
        }
        catch (JsonException error) { throw new InvalidDataException("The signed catalogue is malformed.", error); }
    }

    private sealed record Document(int Schema, Entry[]? Packages);
    private sealed record Entry(string? Version, string? Rid, long Size, string? Sha256, string? ManifestSha256, bool? Revoked);
}

/// <summary>Package content is addressed by its signed hash; no arbitrary URL is accepted.</summary>
public sealed class PackageEntry
{
    internal PackageEntry(string version, string rid, long size, string sha256, string manifestSha256, bool revoked) =>
        (Version, Rid, Size, Sha256, ManifestSha256, Revoked) = (version, rid, size, sha256, manifestSha256, revoked);
    public string Version { get; }
    public string Rid { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public string ManifestSha256 { get; }
    public bool Revoked { get; }
}
