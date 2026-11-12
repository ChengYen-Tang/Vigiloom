using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Processes;

public sealed record ProcessParent(int ProcessId, int ParentProcessId, long CreationMicroseconds);

[SupportedOSPlatform("windows")]
public static class ParentProcessReader
{
    /// <summary>Reads parent PIDs in bulk; creation time is required for a safe join to the process snapshot.</summary>
    public static InventoryReadResult<ProcessParent> Read(CancellationToken token)
    {
        var result = new List<ProcessParent>();
        var partial = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(30) };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT ProcessId, ParentProcessId, CreationDate FROM Win32_Process"), options);
            using var collection = searcher.Get();
            using var iterator = collection.GetEnumerator();
            while (iterator.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                using var row = (ManagementObject)iterator.Current;
                if (result.Count == 20_000) { partial = true; break; }
                if (row["ProcessId"] is not uint id || id > int.MaxValue || row["ParentProcessId"] is not uint parent ||
                    parent > int.MaxValue || row["CreationDate"] is not string raw)
                { partial = true; continue; }
                if (!TryParseCreation(raw, out var creation)) { partial = true; continue; }
                result.Add(new((int)id, (int)parent, creation));
            }
            return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, result);
        }
        catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (Exception ex) when (ex is ManagementException or COMException) { return new(InventoryReadStatus.Error, []); }
    }

    internal static bool TryParseCreation(string raw, out long utcMicroseconds)
    {
        utcMicroseconds = 0;
        if (raw.Length != 25 || raw[21] is not ('+' or '-') ||
            !DateTime.TryParseExact(raw.AsSpan(0, 21), "yyyyMMddHHmmss.ffffff", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local) ||
            !int.TryParse(raw.AsSpan(22, 3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes > 840)
            return false;
        try
        {
            var offset = TimeSpan.FromMinutes(raw[21] == '-' ? -minutes : minutes);
            utcMicroseconds = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).UtcTicks / 10;
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
