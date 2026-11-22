using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using Vigiloom.Contracts.Agent;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Packages;

/// <summary>Prepares a verified Agent archive in a new directory without touching the running version.</summary>
public static class PackageStager
{
    /// <remarks>The caller must separately verify the current assignment and revocation before switching versions.
    /// The installation root must be writable only by the updater's trusted account.</remarks>
    public static async Task<string> PrepareAsync(Stream archive, PackageManifest manifest, AgentVersionTarget target, string installationRoot,
        string runtimeIdentifier, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(target);
        if (!archive.CanRead || !archive.CanSeek) throw new ArgumentException("A readable, seekable package is required.", nameof(archive));
        if (!target.IsValid() || target.ManifestSha256 != manifest.ManifestSha256 ||
            target.PackageVersion != manifest.Version || target.RuntimeIdentifier != manifest.RuntimeIdentifier)
            throw new InvalidDataException("The verified manifest does not match the Agent's assigned version.");
        if (manifest.RuntimeIdentifier != runtimeIdentifier)
            throw new InvalidDataException("The package targets a different platform.");
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
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(stagingRoot, LinuxDirectoryMode);
        if ((ulong)totalBytes > DiskSpace.Available(stagingRoot)) throw new IOException("There is not enough space to prepare the Agent package.");
        var destination = Path.Combine(stagingRoot, Guid.CreateVersion7().ToString("N"));
        try
        {
            zip.ExtractToDirectory(destination);
            token.ThrowIfCancellationRequested();
            var executable = Path.Combine(destination, runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? "Vigiloom.Agent.exe" : "Vigiloom.Agent");
            if (!File.Exists(executable)) throw new InvalidDataException("The Agent executable is missing from the package.");
            if (OperatingSystem.IsLinux()) PrepareLinuxPermissions(destination, executable);
            return destination;
        }
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private const UnixFileMode LinuxDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode LinuxFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    [SupportedOSPlatform("linux")]
    private static void PrepareLinuxPermissions(string directory, string executable)
    {
        // The Updater is root, while the Agent uses a separate account. Version files contain
        // no state or credentials, and must be readable/traversable but not writable by it.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        File.SetUnixFileMode(directory, LinuxDirectoryMode);
        foreach (var child in Directory.EnumerateDirectories(directory, "*", options)) File.SetUnixFileMode(child, LinuxDirectoryMode);
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            var runnable = file == executable || (File.GetUnixFileMode(file) & ExecuteBits) != 0;
            File.SetUnixFileMode(file, LinuxFileMode | (runnable ? ExecuteBits : 0));
        }
    }
}
