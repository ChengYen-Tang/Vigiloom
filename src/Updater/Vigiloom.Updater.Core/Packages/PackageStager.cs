using System.IO.Compression;
using System.Security.Cryptography;
using Vigiloom.Contracts.Agent;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Packages;

/// <summary>Prepares a verified Agent archive in a new directory without touching the running version.</summary>
public static class PackageStager
{
    /// <remarks>The caller must separately verify the current assignment and revocation before switching versions.
    /// The installation root must be writable only by the updater's trusted account.</remarks>
    public static async Task<string> PrepareAsync(Stream archive, PackageManifest manifest, AgentVersionTarget target, string installationRoot,
        string runtimeIdentifier, string updaterVersion, string? currentAgentVersion, TimeProvider clock, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(clock);
        if (!archive.CanRead || !archive.CanSeek) throw new ArgumentException("A readable, seekable package is required.", nameof(archive));
        if (!target.IsValid() || target.ManifestSha256 != manifest.ManifestSha256 ||
            target.PackageVersion != manifest.Version || target.RuntimeIdentifier != manifest.RuntimeIdentifier)
            throw new InvalidDataException("The verified manifest does not match the Agent's assigned version.");
        if (!manifest.IsCompatible(runtimeIdentifier, updaterVersion, currentAgentVersion, clock.GetUtcNow()))
            throw new InvalidDataException("The package is expired or incompatible with this Agent.");
        if (archive.Length != manifest.Size) throw new InvalidDataException("The package length does not match its verified manifest.");
        archive.Position = 0;
        var digest = await SHA256.HashDataAsync(archive, token);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(manifest.Sha256)))
            throw new InvalidDataException("The package hash does not match its verified manifest.");
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var totalBytes = 0L;
        foreach (var entry in zip.Entries) totalBytes = checked(totalBytes + entry.Length);
        var root = Path.GetFullPath(installationRoot);
        Directory.CreateDirectory(root);
        var stagingRoot = Path.Combine(root, "staging");
        Directory.CreateDirectory(stagingRoot);
        if ((ulong)totalBytes > DiskSpace.Available(stagingRoot)) throw new IOException("There is not enough space to prepare the Agent package.");
        var destination = Path.Combine(stagingRoot, Guid.CreateVersion7().ToString("N"));
        try
        {
            zip.ExtractToDirectory(destination);
            token.ThrowIfCancellationRequested();
            var executable = Path.Combine(destination, runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? "Vigiloom.Agent.exe" : "Vigiloom.Agent");
            if (!File.Exists(executable)) throw new InvalidDataException("The Agent executable is missing from the package.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            if (manifest.Expires <= clock.GetUtcNow()) throw new InvalidDataException("The package authorization expired during preparation.");
            return destination;
        }
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
    }
}
