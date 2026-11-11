using System.Text.Json;
using Tuf;

namespace Vigiloom.UpdateSecurity;

/// <summary>Connects TUF's accepted-metadata callbacks to an exclusive durable trust-state lease.</summary>
public sealed class DurableTufCache(ITrustMetadataLease lease) : ITufCache
{
    private readonly ITrustMetadataLease lease = lease ?? throw new ArgumentNullException(nameof(lease));

    public byte[]? LoadMetadata(string role)
    {
        ValidateRole(role);
        var data = lease.LoadMetadata(role);
        if (data is null)
        {
            if (role == "root") throw new InvalidDataException("The trusted root has not been provisioned.");
            return null;
        }
        ValidateMetadata(role, data);
        return data;
    }

    public void StoreMetadata(string role, byte[] data)
    {
        ValidateRole(role);
        ArgumentNullException.ThrowIfNull(data);
        ValidateMetadata(role, data);
        // TUF has already authenticated this metadata; a failed durable write must stop the operation.
        lease.StoreMetadata(role, data);
    }

    // Target contents can be fetched again; only accepted metadata is security state.
    public byte[]? LoadTarget(string targetPath) => null;
    public void StoreTarget(string targetPath, byte[] data) { }

    internal static void ValidateRole(string role)
    {
        if (role is not ("root" or "timestamp" or "snapshot" or "targets"))
            throw new ArgumentException("Unsupported TUF metadata role.", nameof(role));
    }

    internal static int ValidateMetadata(string role, byte[] data)
    {
        if (data.Length is 0 or > BoundedTufRepository.MaximumDocumentBytes)
            throw new InvalidDataException("Invalid trust metadata size.");
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("signed", out var signed) ||
                signed.ValueKind != JsonValueKind.Object || !signed.TryGetProperty("_type", out var type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != role ||
                !signed.TryGetProperty("version", out var version) || !version.TryGetInt32(out var value) || value <= 0 ||
                !root.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array ||
                signatures.GetArrayLength() == 0)
                throw new InvalidDataException("Stored trust metadata has an invalid envelope.");
            return value;
        }
        catch (JsonException error) { throw new InvalidDataException("Stored trust metadata is malformed.", error); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Stored trust metadata has invalid fields.", error); }
    }
}
