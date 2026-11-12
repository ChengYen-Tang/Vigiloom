using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Processes;

namespace Vigiloom.Agent.Platforms.Linux.Processes;

[SupportedOSPlatform("linux")]
public static class ServiceReader
{
    public static async Task<InventoryReadResult<ServiceObservation>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists("/run/systemd/system")) return new(InventoryReadStatus.Unsupported, []);
        const string executable = "/usr/bin/systemctl";
        var files = await CommandReader.ReadAsync(executable,
            ["list-unit-files", "--type=service", "--no-legend", "--no-pager", "--plain"], token);
        var loaded = await CommandReader.ReadAsync(executable,
            ["show", "--all", "--type=service", "--property=Id", "--property=Description", "--property=ActiveState", "--property=UnitFileState", "--no-pager", "*.service"], token);
        if (files.Status is not (InventoryReadStatus.Complete or InventoryReadStatus.Partial) &&
            loaded.Status is not (InventoryReadStatus.Complete or InventoryReadStatus.Partial))
            return new(files.Status == InventoryReadStatus.PermissionDenied || loaded.Status == InventoryReadStatus.PermissionDenied
                ? InventoryReadStatus.PermissionDenied : InventoryReadStatus.Error, []);

        var partial = files.Status != InventoryReadStatus.Complete || loaded.Status != InventoryReadStatus.Complete;
        var entries = new Dictionary<string, ServiceObservation>(StringComparer.Ordinal);
        foreach (var line in files.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !Valid(fields[0]) || !Valid(fields[1])) { partial = true; continue; }
            if (entries.Count >= 20_000) { partial = true; break; }
            entries[fields[0]] = new(fields[0], fields[0], loaded.Status == InventoryReadStatus.Complete ? "not-loaded" : "unknown", fields[1]);
        }
        foreach (var block in loaded.Output.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            string? id = null, name = null, status = null, mode = null;
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = line.IndexOf('=');
                if (split < 0) { partial = true; continue; }
                var value = line[(split + 1)..].TrimEnd('\r');
                switch (line[..split])
                {
                    case "Id": id = value; break;
                    case "Description": name = value; break;
                    case "ActiveState": status = value; break;
                    case "UnitFileState": mode = value; break;
                }
            }
            if (!Valid(id) || !Valid(status)) { partial = true; continue; }
            if (!entries.ContainsKey(id!) && entries.Count >= 20_000) { partial = true; break; }
            var display = Valid(name) ? name! : id!;
            var startup = Valid(mode) ? mode : entries.GetValueOrDefault(id!)?.StartupMode;
            entries[id!] = new(id!, display, status!, startup);
        }
        return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, entries.Values.ToArray());
    }

    private static bool Valid(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 2048 && !text.Any(char.IsControl);
}
