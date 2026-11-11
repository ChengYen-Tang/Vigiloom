using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Inventory;

internal static class PackageFields
{
    internal static (PackageCommand.LineStatus, InstalledSoftware?) Read(string[] columns, int name, int version, int architecture, int supplier, bool multiVersion)
    {
        if (columns.Any(value => value.Length > InventoryChunkCodec.MaximumEntryTextLength || value.Any(char.IsControl)) ||
            string.IsNullOrWhiteSpace(columns[name]) || string.IsNullOrWhiteSpace(columns[version]) ||
            string.IsNullOrWhiteSpace(columns[architecture]) || columns[architecture].Length > InventoryChunkCodec.MaximumArchitectureLength) return (PackageCommand.LineStatus.Invalid, null);
        // RPM supports multiple versions of the same package and architecture concurrently.
        var key = multiVersion ? $"{columns[name]}:{columns[architecture]}:{columns[version]}" : columns[name];
        if (key.Length > InventoryChunkCodec.MaximumEntryTextLength) return (PackageCommand.LineStatus.Invalid, null);
        return (PackageCommand.LineStatus.Installed, new(key, columns[name], columns[version],
            string.IsNullOrWhiteSpace(columns[supplier]) || columns[supplier] == "(none)" ? null : columns[supplier], columns[architecture]));
    }
}
