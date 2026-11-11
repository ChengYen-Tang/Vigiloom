using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tuf;

namespace Vigiloom.UpdateSecurity.Tests;

internal sealed class TestRepository : ITufRepository
{
    private readonly byte[] catalogue;
    private readonly string targetName;
    private readonly Dictionary<string, byte[]> metadata;
    private readonly IReadOnlyDictionary<string, byte[]> additionalTargets;
    public byte[]? RotatedRoot { get; set; }
    public bool MissingTarget { get; set; }
    public Func<CancellationToken, Task>? BeforeTarget { get; set; }

    public TestRepository(ECDsa key, int timestampVersion = 1, byte[]? catalogue = null, int metadataVersion = 1, string targetName = "catalog.json",
        IReadOnlyDictionary<string, byte[]>? additionalTargets = null)
    {
        this.catalogue = catalogue ?? "catalogue"u8.ToArray();
        this.targetName = targetName;
        this.additionalTargets = additionalTargets ?? new Dictionary<string, byte[]>();
        var targetMetadata = new Dictionary<string, object> { [targetName] = Meta(this.catalogue, includeVersion: false) };
        foreach (var (name, bytes) in this.additionalTargets) targetMetadata.Add(name, Meta(bytes, includeVersion: false));
        var targets = Sign(new Dictionary<string, object>
        {
            ["_type"] = "targets", ["expires"] = Expiry, ["spec_version"] = "1.0.0", ["version"] = metadataVersion,
            ["targets"] = targetMetadata
        }, key);
        var snapshot = Sign(new Dictionary<string, object>
        {
            ["_type"] = "snapshot", ["expires"] = Expiry, ["spec_version"] = "1.0.0", ["version"] = metadataVersion,
            ["meta"] = new Dictionary<string, object> { ["targets.json"] = Meta(targets, includeVersion: true, metadataVersion) }
        }, key);
        var timestamp = Sign(new Dictionary<string, object>
        {
            ["_type"] = "timestamp", ["expires"] = Expiry, ["spec_version"] = "1.0.0", ["version"] = timestampVersion,
            ["meta"] = new Dictionary<string, object> { ["snapshot.json"] = Meta(snapshot, includeVersion: true, metadataVersion) }
        }, key);
        metadata = new() { ["targets"] = targets, ["snapshot"] = snapshot, ["timestamp"] = timestamp };
    }

    public byte[] Root(int version, ECDsa key, ECDsa? previous = null, string? omitRole = null)
    {
        var keyId = KeyId(key);
        var roles = new Dictionary<string, object>();
        foreach (var name in new[] { "root", "timestamp", "snapshot", "targets" })
            if (name != omitRole) roles[name] = new Dictionary<string, object> { ["keyids"] = new[] { keyId }, ["threshold"] = 1 };
        var root = new Dictionary<string, object>
        {
            ["_type"] = "root", ["consistent_snapshot"] = false, ["expires"] = Expiry,
            ["keys"] = new Dictionary<string, object> { [keyId] = KeyEntry(key) }, ["roles"] = roles,
            ["spec_version"] = "1.0.0", ["version"] = version
        };
        return Sign(root, key, previous);
    }

    public Task<byte[]?> FetchMetadataAsync(string role, int? version = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(role == "root" ? version == 2 ? RotatedRoot : null : metadata.GetValueOrDefault(role));
    }
    public async Task<byte[]?> FetchTargetAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeTarget is not null) await BeforeTarget(cancellationToken);
        if (MissingTarget) return null;
        return targetPath == targetName ? catalogue : additionalTargets.GetValueOrDefault(targetPath);
    }

    private static string Expiry => DateTimeOffset.UtcNow.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    private static Dictionary<string, object> Meta(byte[] content, bool includeVersion, int version = 1)
    {
        var value = new Dictionary<string, object> { ["length"] = content.Length,
            ["hashes"] = new Dictionary<string, object> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(content)) } };
        if (includeVersion) value["version"] = version;
        return value;
    }
    private static Dictionary<string, object> KeyEntry(ECDsa key) => new()
    {
        ["keytype"] = "ecdsa", ["scheme"] = "ecdsa-sha2-nistp256",
        ["keyval"] = new Dictionary<string, object> { ["public"] = key.ExportSubjectPublicKeyInfoPem() }
    };
    private static string KeyId(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(KeyEntry(key)))));
    private static byte[] Sign(Dictionary<string, object> signed, ECDsa key, ECDsa? second = null)
    {
        var bytes = Encoding.UTF8.GetBytes(Canonical(signed));
        object Signature(ECDsa signer) => new Dictionary<string, object>
        {
            ["keyid"] = KeyId(signer),
            ["sig"] = Convert.ToHexStringLower(signer.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        };
        var signatures = second is null ? new[] { Signature(key) } : new[] { Signature(key), Signature(second) };
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["signed"] = signed, ["signatures"] = signatures });
    }
    private static string Canonical(object value) => value switch
    {
        string text => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
        bool boolean => boolean ? "true" : "false",
        int number => number.ToString(CultureInfo.InvariantCulture),
        Dictionary<string, object> fields => "{" + string.Join(',', fields.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => Canonical(x.Key) + ":" + Canonical(x.Value))) + "}",
        string[] values => "[" + string.Join(',', values.Select(Canonical)) + "]",
        _ => throw new InvalidOperationException("Unexpected fixture value.")
    };
}
