using System.Net.NetworkInformation;
using System.Net.Sockets;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Agent.Core.Metrics;

namespace Vigiloom.Agent.Core.Inventory;

/// <summary>Collects OS-visible interfaces, addresses and mounted volumes; these are not physical-device inventories.</summary>
public static class HostHardwareReader
{
    public static InventoryReadResult<InventoryEntry> ReadNetwork(CancellationToken token)
    {
        var result = new List<InventoryEntry>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var partial = false;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                token.ThrowIfCancellationRequested();
                var key = $"interface:{adapter.Id}";
                var name = string.IsNullOrWhiteSpace(adapter.Name) ? adapter.Id : adapter.Name;
                if (!Valid(key, 1024) || !Valid(name, 2048) || !keys.Add(key)) { partial = true; continue; }
                if (result.Count >= InventoryChunkCodec.MaximumSnapshotItems) { partial = true; break; }
                result.Add(new(key, name, ResourceType: "interface", HardwareType: adapter.NetworkInterfaceType.ToString()));
                try
                {
                    foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                    {
                        token.ThrowIfCancellationRequested();
                        if (address.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;
                        var text = address.Address.ToString();
                        var addressKey = $"address:{adapter.Id}|{text}";
                        if (!Valid(text, 256) || !Valid(addressKey, 2048)) { partial = true; continue; }
                        if (!keys.Add(addressKey)) continue;
                        if (result.Count >= InventoryChunkCodec.MaximumSnapshotItems) { partial = true; break; }
                        result.Add(new(addressKey, text, ResourceType: "address", ParentKey: key, Address: text));
                    }
                }
                catch (Exception ex) when (ex is NetworkInformationException or SocketException or PlatformNotSupportedException)
                { partial = true; }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or PlatformNotSupportedException)
        { return new(InventoryReadStatus.Error, []); }
        return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, result);
    }

    public static InventoryReadResult<InventoryEntry> FromVolumes(VolumeSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var result = new List<InventoryEntry>(sample.Volumes.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var partial = sample.Truncated || sample.UnavailableVolumes > 0;
        foreach (var volume in sample.Volumes)
        {
            if (!Valid(volume.Mount, 2048) || !keys.Add(volume.Mount) || volume.TotalBytes <= 0) { partial = true; continue; }
            if (result.Count >= InventoryChunkCodec.MaximumSnapshotItems) { partial = true; break; }
            result.Add(new(volume.Mount, volume.Mount, ResourceType: "volume", CapacityBytes: volume.TotalBytes));
        }
        return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, result);
    }

    private static bool Valid(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);
}
