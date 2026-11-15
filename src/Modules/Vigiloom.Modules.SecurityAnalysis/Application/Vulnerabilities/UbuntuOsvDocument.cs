using System.IO.Compression;
using System.Security.Cryptography;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal readonly record struct UbuntuPackageScope(string Ecosystem, string SourcePackage);

/// <summary>A validated upstream document and its distinct discovery keys, prepared for content-addressed storage.</summary>
internal sealed record UbuntuOsvDocument(byte[] Digest, byte[] Compressed, int OriginalLength, UbuntuPackageScope[] Scopes,
    DateTimeOffset ModifiedAt = default, DateTimeOffset? WithdrawnAt = null)
{
    internal static async ValueTask<UbuntuOsvDocument> CreateAsync(UbuntuOsvRecord record, ReadOnlyMemory<byte> raw, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (raw.IsEmpty) throw new ArgumentException("The original document is required.", nameof(raw));
        var digest = SHA256.HashData(raw.Span);
        var scopes = new HashSet<UbuntuPackageScope>();
        foreach (var package in record.Packages)
            scopes.Add(new(package.Ecosystem, package.SourcePackage));

        using var output = new MemoryStream();
        await using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            await compressor.WriteAsync(raw, token);
        return new(digest, output.ToArray(), raw.Length, [.. scopes], record.ModifiedAt, record.WithdrawnAt);
    }
}
