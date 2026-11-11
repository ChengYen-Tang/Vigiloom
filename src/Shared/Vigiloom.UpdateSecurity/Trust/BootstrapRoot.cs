using Tuf;
using System.Text.Json;

namespace Vigiloom.UpdateSecurity;

/// <summary>Validates an independently obtained root before it is committed as the initial trust anchor.</summary>
public static class BootstrapRoot
{
    /// <remarks>
    /// Self-signature validation detects a broken root; it does not establish publisher identity.
    /// The caller must authenticate the root through an independent trusted channel. Expired
    /// bootstrap roots are allowed so that the normal TUF workflow can rotate them forward.
    /// </remarks>
    public static async Task ValidateAsync(byte[] root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        DurableTufCache.ValidateMetadata("root", root);
        ValidateRoles(root);
        using var client = new TufClient(new TufClientOptions
        {
            MetadataBaseUrl = new Uri("https://bootstrap.invalid/metadata/"),
            TrustedRoot = root,
            Repository = RootValidationRepository.Instance
        });
        try
        {
            // The TUF client validates its trusted root before it asks for the next root.
            // Stop at that boundary; bootstrap validation must never contact a repository.
            await client.GetTrustedMetadataAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException("The TUF client did not validate the initial root.");
        }
        catch (RootValidatedException) { }
    }

    private static void ValidateRoles(byte[] root)
    {
        try
        {
            using var document = JsonDocument.Parse(root, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
            var signed = document.RootElement.GetProperty("signed");
            var keys = signed.GetProperty("keys");
            var roles = signed.GetProperty("roles");
            foreach (var name in new[] { "root", "timestamp", "snapshot", "targets" })
            {
                var role = roles.GetProperty(name);
                var threshold = role.GetProperty("threshold").GetInt32();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in role.GetProperty("keyids").EnumerateArray())
                {
                    var id = key.GetString();
                    if (string.IsNullOrEmpty(id) || !ids.Add(id) || !keys.TryGetProperty(id, out _))
                        throw new InvalidDataException("A required TUF role has invalid keys.");
                }
                if (threshold < 1 || threshold > ids.Count)
                    throw new InvalidDataException("A required TUF role has an invalid threshold.");
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The initial root has invalid TUF roles.", error);
        }
    }

    private sealed class RootValidationRepository : ITufRepository
    {
        public static readonly RootValidationRepository Instance = new();
        public Task<byte[]?> FetchMetadataAsync(string role, int? version = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (role != "root" || version is not > 0)
                throw new InvalidOperationException("The TUF bootstrap validation sequence has changed.");
            throw new RootValidatedException();
        }
        public Task<byte[]?> FetchTargetAsync(string targetPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Root validation must not request targets.");
    }

    private sealed class RootValidatedException : Exception;
}
