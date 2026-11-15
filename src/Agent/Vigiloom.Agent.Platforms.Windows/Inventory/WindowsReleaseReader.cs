using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Inventory;

/// <summary>Registry-reported release attributes; these do not establish servicing or update applicability.</summary>
public sealed record WindowsRelease(string Edition, string? Release, int Build, int Revision, string? InstallationType = null);

[SupportedOSPlatform("windows")]
public static class WindowsReleaseReader
{
    private const string Path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static InventoryReadResult<WindowsRelease> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
            using var key = root.OpenSubKey(Path, writable: false);
            if (key is null || !RegistryRead.Stamp(key.Handle, out var count, out var stamp))
                return new(InventoryReadStatus.Error, []);
            if (!RegistryRead.Text(key.Handle, "EditionID", out var edition) ||
                !RegistryRead.Text(key.Handle, "DisplayVersion", out var release) ||
                !RegistryRead.Text(key.Handle, "CurrentBuildNumber", out var build) ||
                !RegistryRead.Text(key.Handle, "InstallationType", out var installationType) ||
                !RegistryRead.Dword(key.Handle, "UBR", out var revision)) return new(InventoryReadStatus.Partial, []);
            if (release is null && !RegistryRead.Text(key.Handle, "ReleaseId", out release)) return new(InventoryReadStatus.Partial, []);
            token.ThrowIfCancellationRequested();
            if (!RegistryRead.Stamp(key.Handle, out var finalCount, out var finalStamp) || count != finalCount || stamp != finalStamp ||
                !TryCreate(edition, release, build, revision, out var result, installationType))
                return new(InventoryReadStatus.Partial, []);
            return new(InventoryReadStatus.Complete, [result!]);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (IOException) { return new(InventoryReadStatus.Error, []); }
    }

    internal static bool TryCreate(object? edition, object? release, object? build, object? revision, out WindowsRelease? result, object? installationType = null)
    {
        result = null;
        if (edition is not string name || !ValidText(name) || release is not (null or string) ||
            release is string label && !ValidText(label) || build is not string number ||
            !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var buildNumber) || buildNumber <= 0 ||
            revision is not int revisionNumber || revisionNumber < 0 || installationType is not (null or string) ||
            installationType is string type && !ValidText(type)) return false;
        result = new(name, (string?)release, buildNumber, revisionNumber, (string?)installationType);
        return true;
    }

    private static bool ValidText(string value) => value.Length <= 256 && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
