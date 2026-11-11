using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Inventory;

/// <summary>Reads the Windows CBS quick-fix view; it does not enumerate all available or installed updates.</summary>
[SupportedOSPlatform("windows")]
public static class QuickFixReader
{
    private const int MaximumEntries = 20_000;
    private const int MaximumLength = 2048;

    public static InventoryReadResult<InstalledPatch> Read(CancellationToken token)
    {
        var items = new List<InstalledPatch>();
        var partial = false;
        try
        {
            token.ThrowIfCancellationRequested();
            // WMI applies this timeout to enumeration waits; it is not a total query deadline.
            var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(30) };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering"), options);
            using var results = searcher.Get();
            using var enumerator = results.GetEnumerator();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext()) break;
                using (var value = (ManagementObject)enumerator.Current)
                {
                    token.ThrowIfCancellationRequested();
                    if (items.Count >= MaximumEntries) { partial = true; break; }
                    var id = value["HotFixID"];
                    var description = value["Description"];
                    var installedOn = value["InstalledOn"];
                    if (id is not string key || description is not (null or string) || installedOn is not (null or string) ||
                        !TryCreate(key, (string?)description, (string?)installedOn, out var patch)) { partial = true; continue; }
                    items.Add(patch!);
                }
            }
            token.ThrowIfCancellationRequested();
            return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, items);
        }
        catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (Exception ex) when (ex is ManagementException or COMException) { return new(InventoryReadStatus.Error, []); }
    }

    internal static bool TryCreate(string id, string? description, string? installedOn, out InstalledPatch? patch)
    {
        patch = null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaximumLength || id.Any(char.IsControl) ||
            description?.Length > MaximumLength || installedOn?.Length > MaximumLength ||
            (description?.Any(char.IsControl) ?? false) || (installedOn?.Any(char.IsControl) ?? false)) return false;
        patch = new(id, string.IsNullOrWhiteSpace(description) ? null : description,
            string.IsNullOrWhiteSpace(installedOn) ? null : installedOn);
        return true;
    }
}
