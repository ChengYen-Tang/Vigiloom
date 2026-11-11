using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Inventory;

/// <summary>Reads one machine uninstall registry view without executing installer actions.</summary>
[SupportedOSPlatform("windows")]
public static class UninstallRegistryReader
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int MaximumEntries = 20_000;
    private const int MaximumRetainedCharacters = 4_000_000;

    public static InventoryReadResult<InstalledSoftware> Read(RegistryView view, CancellationToken token)
    {
        if (view is not (RegistryView.Registry32 or RegistryView.Registry64))
            throw new ArgumentOutOfRangeException(nameof(view));
        if (view == RegistryView.Registry64 && !Environment.Is64BitOperatingSystem)
            return new(InventoryReadStatus.Unsupported, []);

        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = root.OpenSubKey(UninstallPath, writable: false);
            if (uninstall is null) return new(InventoryReadStatus.Error, []);
            return ReadKey(uninstall, token);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new(InventoryReadStatus.PermissionDenied, []);
        }
        catch (IOException)
        {
            return new(InventoryReadStatus.Error, []);
        }
    }

    internal static InventoryReadResult<InstalledSoftware> ReadKey(RegistryKey uninstall, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!RegistryRead.Stamp(uninstall.Handle, out var initialCount, out var initialWrite))
            return new(InventoryReadStatus.Error, []);
        var entries = new List<InstalledSoftware>();
        var retainedCharacters = 0;
        var partial = false;
        for (uint index = 0; ; index++)
        {
            token.ThrowIfCancellationRequested();
            var status = RegistryRead.Name(uninstall.Handle, index, out var name);
            if (status == RegistryRead.NoMoreItems) break;
            if (status != 0 || index >= MaximumEntries || name is null) { partial = true; break; }
            try
            {
                using var key = uninstall.OpenSubKey(name, writable: false);
                if (key is null) { partial = true; continue; }
                if (!RegistryRead.Stamp(key.Handle, out var beforeCount, out var beforeWrite)) { partial = true; continue; }
                if (!RegistryRead.Text(key.Handle, "DisplayName", out var displayName)) { partial = true; continue; }
                if (!RegistryRead.Text(key.Handle, "DisplayVersion", out var version) ||
                    !RegistryRead.Text(key.Handle, "Publisher", out var publisher)) { partial = true; continue; }
                if (!RegistryRead.Stamp(key.Handle, out var afterCount, out var afterWrite) ||
                    beforeCount != afterCount || beforeWrite != afterWrite) { partial = true; continue; }
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                if (displayName.Any(char.IsControl) || version?.Any(char.IsControl) == true || publisher?.Any(char.IsControl) == true)
                { partial = true; continue; }
                if (string.IsNullOrWhiteSpace(version)) version = null;
                if (string.IsNullOrWhiteSpace(publisher)) publisher = null;
                var characters = name.Length + displayName.Length + (version?.Length ?? 0) + (publisher?.Length ?? 0);
                if (characters > MaximumRetainedCharacters - retainedCharacters) { partial = true; break; }
                retainedCharacters += characters;
                entries.Add(new(name, displayName, version, publisher));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                partial = true;
            }
        }
        if (!RegistryRead.Stamp(uninstall.Handle, out var finalCount, out var finalWrite) ||
            initialCount != finalCount || initialWrite != finalWrite) partial = true;
        return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, entries);
    }
}
