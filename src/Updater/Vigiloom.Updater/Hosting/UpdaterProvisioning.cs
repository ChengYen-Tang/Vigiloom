using System.Security.Cryptography;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Hosting;

/// <summary>Installer entry point for an independently trusted initial publisher root.</summary>
internal static class UpdaterProvisioning
{
    public static async Task ProvisionTrustAsync(IConfiguration configuration, string rootPath, string fingerprint, CancellationToken token)
    {
        var state = configuration["Vigiloom:Updater:StateDirectory"];
        if (string.IsNullOrWhiteSpace(state) || !Path.IsPathFullyQualified(state) || Path.GetPathRoot(state) == Path.GetFullPath(state))
            throw new ArgumentException("An absolute, non-root updater state directory is required.");
        if (!Path.IsPathFullyQualified(rootPath)) throw new ArgumentException("An absolute trusted-root file is required.", nameof(rootPath));
        if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A SHA-256 fingerprint is required.", nameof(fingerprint));
        await using var file = new FileStream(rootPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("The trusted-root file has an invalid size.");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(fingerprint)))
            throw new InvalidDataException("The trusted-root fingerprint does not match.");
        await new LocalTrustStore(Path.Combine(state, "trust")).ProvisionAsync(bytes, token);
    }
}
