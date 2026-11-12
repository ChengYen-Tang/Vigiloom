using System.Globalization;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Processes;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Processes;

[SupportedOSPlatform("linux")]
public static class ListenerReader
{
    public static async Task<InventoryReadResult<ListenerObservation>> ReadAsync(CancellationToken token)
    {
        var command = await CommandReader.ReadAsync("/usr/bin/ss", ["-H", "-l", "-n", "-t", "-u", "-p"], token);
        if (command.Status is not (InventoryReadStatus.Complete or InventoryReadStatus.Partial)) return new(command.Status, []);
        var partial = command.Status == InventoryReadStatus.Partial;
        var reasons = partial ? InventoryPartialReason.Truncated : 0;
        var result = new List<ListenerObservation>();
        foreach (var line in command.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || fields[0] is not ("tcp" or "udp")) { partial = true; continue; }
            var local = fields[4];
            var separator = local.LastIndexOf(':');
            if (separator < 1 || !int.TryParse(local.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            { partial = true; continue; }
            var address = local[..separator];
            if (address.StartsWith('[') && address.EndsWith(']')) address = address[1..^1];
            if (string.IsNullOrWhiteSpace(address) || address.Length > 256 || address.Any(char.IsControl)) { partial = true; continue; }
            var (ids, overflow) = ReadProcessIds(line);
            partial |= overflow;
            if (overflow) reasons |= InventoryPartialReason.Truncated;
            if (ids.Count == 0) { partial = true; reasons |= InventoryPartialReason.OwnerUnavailable; ids.Add(null); }
            foreach (var id in ids)
            {
                if (result.Count >= 20_000) { partial = true; reasons |= InventoryPartialReason.Truncated; break; }
                result.Add(new(fields[0], address, port, id));
            }
        }
        return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, result,
            partial ? reasons == 0 ? InventoryPartialReason.Other : reasons : null);
    }

    internal static (HashSet<int?> Ids, bool Overflow) ReadProcessIds(string line)
    {
        var result = new HashSet<int?>();
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (quoted && escaped) { escaped = false; continue; }
            if (quoted && current == '\\') { escaped = true; continue; }
            if (current == '"') { quoted = !quoted; continue; }
            if (quoted || !line.AsSpan(index).StartsWith(",pid=", StringComparison.Ordinal)) continue;
            var remaining = line.AsSpan(index + 5);
            var length = 0;
            while (length < remaining.Length && char.IsAsciiDigit(remaining[length])) length++;
            if (length > 0 && int.TryParse(remaining[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                if (result.Count >= 20_000 && !result.Contains(pid)) return (result, true);
                result.Add(pid);
            }
            index += 4 + length;
        }
        return (result, false);
    }
}
