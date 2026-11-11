using System.Security.Cryptography;
using System.Text.Json;

namespace Vigiloom.UpdateSecurity;

/// <summary>A complete, integrity-checked snapshot of accepted TUF roles.</summary>
public sealed class TrustMetadataSnapshot
{
    internal const int MaximumSnapshotBytes = 6 * 1024 * 1024;
    private readonly Dictionary<string, byte[]> roles;
    private TrustMetadataSnapshot(Dictionary<string, byte[]> roles) => this.roles = roles;

    public static TrustMetadataSnapshot Provision(byte[] trustedRoot)
    {
        ArgumentNullException.ThrowIfNull(trustedRoot);
        DurableTufCache.ValidateMetadata("root", trustedRoot);
        return new(new(StringComparer.Ordinal) { ["root"] = trustedRoot.ToArray() });
    }

    public static TrustMetadataSnapshot Decode(byte[] data, byte[] digest)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(digest);
        if (data.Length is 0 or > MaximumSnapshotBytes || digest.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), digest))
            throw new InvalidDataException("Trust snapshot failed its integrity check.");
        try
        {
            var roles = JsonSerializer.Deserialize<Dictionary<string, byte[]>>(data)
                ?? throw new InvalidDataException("Trust snapshot is empty.");
            if (!roles.ContainsKey("root") || roles.Count > 4) throw new InvalidDataException("Trust snapshot is incomplete.");
            foreach (var (role, metadata) in roles)
            {
                DurableTufCache.ValidateRole(role);
                if (metadata is null) throw new InvalidDataException("Trust metadata is missing.");
                DurableTufCache.ValidateMetadata(role, metadata);
            }
            return new(new Dictionary<string, byte[]>(roles, StringComparer.Ordinal));
        }
        catch (JsonException error) { throw new InvalidDataException("Trust snapshot is malformed.", error); }
        catch (ArgumentException error) { throw new InvalidDataException("Trust snapshot has an invalid role.", error); }
    }

    public byte[]? Read(string role)
    {
        DurableTufCache.ValidateRole(role);
        return roles.TryGetValue(role, out var data) ? data.ToArray() : null;
    }

    public TrustMetadataSnapshot WithMetadata(string role, ReadOnlySpan<byte> metadata)
    {
        DurableTufCache.ValidateRole(role);
        var data = metadata.ToArray();
        var version = DurableTufCache.ValidateMetadata(role, data);
        if (roles.TryGetValue(role, out var old))
        {
            var oldVersion = DurableTufCache.ValidateMetadata(role, old);
            if (version < oldVersion || version == oldVersion && !data.AsSpan().SequenceEqual(old))
                throw new InvalidDataException("Trust metadata cannot replace an accepted version.");
        }
        var copy = new Dictionary<string, byte[]>(roles, StringComparer.Ordinal) { [role] = data };
        // Root was authenticated by TUF before StoreMetadata. Reset both online-role
        // baselines atomically with that root when either role's authority changes.
        if (role == "root" && roles.TryGetValue("root", out var previousRoot) &&
            (roles.ContainsKey("timestamp") || roles.ContainsKey("snapshot")) &&
            AuthorityChanged(previousRoot, data, "timestamp", "snapshot"))
        {
            copy.Remove("timestamp");
            copy.Remove("snapshot");
        }
        if (role == "root" && roles.TryGetValue("root", out previousRoot) && roles.ContainsKey("targets") &&
            AuthorityChanged(previousRoot, data, "targets"))
            copy.Remove("targets");
        return new(copy);
    }

    private static bool AuthorityChanged(byte[] previousRoot, byte[] newRoot, params string[] names)
    {
        try
        {
            using var previous = JsonDocument.Parse(previousRoot);
            using var next = JsonDocument.Parse(newRoot);
            var oldRoles = previous.RootElement.GetProperty("signed").GetProperty("roles");
            var newRoles = next.RootElement.GetProperty("signed").GetProperty("roles");
            foreach (var name in names)
            {
                var oldRole = oldRoles.GetProperty(name);
                var newRole = newRoles.GetProperty(name);
                var oldIds = oldRole.GetProperty("keyids").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
                var newIds = newRole.GetProperty("keyids").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
                if (oldRole.GetProperty("threshold").GetInt32() != newRole.GetProperty("threshold").GetInt32() || !oldIds.SetEquals(newIds))
                    return true;
            }
            return false;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Trusted root has invalid online-role authorities.", error);
        }
    }

    public (byte[] Data, byte[] Digest) Encode()
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(roles);
        if (data.Length > MaximumSnapshotBytes) throw new InvalidDataException("Trust snapshot exceeds the allowed size.");
        return (data, SHA256.HashData(data));
    }
}
