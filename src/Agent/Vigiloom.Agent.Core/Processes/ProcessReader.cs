using System.ComponentModel;
using System.Diagnostics;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Core.Processes;

/// <summary>A point-in-time observation, not a process creation or termination event.</summary>
public sealed record ProcessObservation(int ProcessId, DateTimeOffset StartedAt, string Name, string StableKey,
    int? ParentProcessId = null, string? RunAs = null, string? ExecutablePath = null);

public static class ProcessReader
{
    public const int MaximumProcesses = 20_000;

    public static InventoryReadResult<ProcessObservation> Read(CancellationToken token, bool includeExecutablePath = false)
    {
        token.ThrowIfCancellationRequested();
        string? linuxBoot = null;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var value = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                if (!Guid.TryParseExact(value, "D", out var boot) || boot == Guid.Empty) return new(InventoryReadStatus.Error, []);
                linuxBoot = boot.ToString("D");
            }
            catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
            catch (IOException) { return new(InventoryReadStatus.Error, []); }
        }
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        { return new(InventoryReadStatus.Error, []); }

        var result = new List<ProcessObservation>(Math.Min(processes.Length, MaximumProcesses));
        var partial = processes.Length > MaximumProcesses;
        var reasons = partial ? InventoryPartialReason.Truncated : 0;
        try
        {
            for (var index = 0; index < processes.Length && index < MaximumProcesses; index++)
            {
                token.ThrowIfCancellationRequested();
                var process = processes[index];
                try
                {
                    var id = process.Id;
                    process.Refresh();
                    ulong? nativeStart = linuxBoot is null ? null : ReadLinuxStartTicks(id);
                    var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    var name = process.ProcessName;
                    if (started == default || string.IsNullOrWhiteSpace(name) || name.Length > 2048 || name.Any(char.IsControl))
                    { partial = true; continue; }

                    // StartTime is cached: use a fresh object to avoid associating a reused PID
                    // with the first process's information.
                    int? parent = null;
                    string? account = null;
                    string? executablePath = null;
                    if (linuxBoot is not null)
                    {
                        try { (parent, account) = ReadLinuxIdentity(id); }
                        catch (UnauthorizedAccessException) { partial = true; reasons |= InventoryPartialReason.AccessDenied; }
                        catch (IOException) { if (Directory.Exists($"/proc/{id}")) partial = true; }
                        if (parent is null || account is null) partial = true;
                        if (includeExecutablePath)
                        {
                            try
                            {
                                var path = File.ResolveLinkTarget($"/proc/{id}/exe", false)?.FullName;
                                if (ValidPath(path)) executablePath = path;
                                else partial = true;
                            }
                            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { partial = true; if (ex is UnauthorizedAccessException) reasons |= InventoryPartialReason.AccessDenied; }
                        }
                    }
                    using var verify = Process.GetProcessById(id);
                    if (linuxBoot is null)
                    {
                        if (verify.StartTime.ToUniversalTime().Ticks != started.UtcTicks) continue;
                    }
                    else if (nativeStart != ReadLinuxStartTicks(id)) continue;
                    var stableKey = linuxBoot is null ? $"windows:{id}:{started.UtcTicks}" : $"linux:{linuxBoot}:{id}:{nativeStart}";
                    result.Add(new(id, started, name, stableKey, parent, account, executablePath));
                }
                catch (ArgumentException) { /* The process exited during this snapshot. */ }
                catch (InvalidOperationException) { /* The process exited during this snapshot. */ }
                catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or NotSupportedException)
                { partial = true; if (ex is UnauthorizedAccessException || ex is Win32Exception { NativeErrorCode: 5 or 13 }) reasons |= InventoryPartialReason.AccessDenied; }
                catch (IOException) { if (Directory.Exists($"/proc/{process.Id}")) partial = true; }
            }
            return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, result,
                partial ? reasons == 0 ? InventoryPartialReason.Other : reasons : null);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    internal static bool ValidPath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 2000 && !path.Any(char.IsControl);

    internal static ulong ReadLinuxStartTicks(int processId)
    {
        var text = File.ReadAllText($"/proc/{processId}/stat");
        var close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length) throw new IOException("Invalid process stat.");
        var fields = text.AsSpan(close + 2);
        for (var field = 0; field < 19; field++)
        {
            var separator = fields.IndexOf(' ');
            if (separator < 0) throw new IOException("Incomplete process stat.");
            fields = fields[(separator + 1)..].TrimStart();
        }
        var end = fields.IndexOf(' ');
        var start = end < 0 ? fields : fields[..end];
        if (!ulong.TryParse(start, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks))
            throw new IOException("Invalid process start tick.");
        return ticks;
    }

    private static (int? Parent, string? Account) ReadLinuxIdentity(int processId)
    {
        int? parent = null;
        string? account = null;
        foreach (var line in File.ReadLines($"/proc/{processId}/status"))
        {
            if (line.StartsWith("PPid:", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(5).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid >= 0)
                parent = pid;
            else if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                if (TryEffectiveUid(line.AsSpan(4), out var uid)) account = $"uid:{uid}";
            }
            if (parent is not null && account is not null) break;
        }
        return (parent, account);
    }

    internal static bool TryEffectiveUid(ReadOnlySpan<char> fields, out uint uid)
    {
        uid = 0;
        fields = fields.TrimStart();
        var separator = fields.IndexOfAny(' ', '\t');
        if (separator < 0) return false;
        fields = fields[separator..].TrimStart();
        separator = fields.IndexOfAny(' ', '\t');
        if (separator >= 0) fields = fields[..separator];
        return uint.TryParse(fields, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uid);
    }
}
