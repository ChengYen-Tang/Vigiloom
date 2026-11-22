using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Vigiloom.UpdateSecurity;

/// <summary>Details for one package, independent of central assignment.</summary>
public sealed class PackageManifest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8 };
    private PackageManifest(Document value, string manifestSha256)
    {
        Version = value.Version!; RuntimeIdentifier = value.Rid!; Size = value.Size; Sha256 = value.Sha256!;
        ManifestSha256 = manifestSha256;
    }

    public string Version { get; }
    public string RuntimeIdentifier { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public string ManifestSha256 { get; }

    public static PackageManifest Parse(VerifiedManifest verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        try
        {
            var value = JsonSerializer.Deserialize<Document>(verified.Content.Span, Options)
                ?? throw new InvalidDataException("The package manifest is empty.");
            if (value.Schema != 1 || !PackageRules.TryVersion(value.Version, out _) || !PackageRules.IsRuntime(value.Rid) ||
                value.Size <= 0 || !PackageRules.IsHash(value.Sha256))
                throw new InvalidDataException("The package manifest contains invalid package metadata.");
            var digest = Convert.ToHexStringLower(SHA256.HashData(verified.Content.Span));
            return new PackageManifest(value, digest);
        }
        catch (JsonException error) { throw new InvalidDataException("The package manifest is malformed.", error); }
    }

    private sealed record Document(int Schema, string? Version, string? Rid, long Size, string? Sha256);
}
