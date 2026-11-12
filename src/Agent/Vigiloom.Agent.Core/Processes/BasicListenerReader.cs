using System.Net.NetworkInformation;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Core.Processes;

/// <summary>Fallback endpoint observation when the OS-specific owner source is unavailable.</summary>
public static class BasicListenerReader
{
    public static InventoryReadResult<ListenerObservation> Read(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var tcp = properties.GetActiveTcpListeners();
            var udp = properties.GetActiveUdpListeners();
            var result = new List<ListenerObservation>(Math.Min(tcp.Length + udp.Length, 20_000));
            foreach (var (protocol, endpoints) in new[] { ("tcp", tcp), ("udp", udp) })
            {
                foreach (var endpoint in endpoints)
                {
                    token.ThrowIfCancellationRequested();
                    if (result.Count == 20_000) return new(InventoryReadStatus.Partial, result);
                    if (endpoint.Port > 0) result.Add(new(protocol, endpoint.Address.ToString(), endpoint.Port, null));
                }
            }
            // The managed API does not expose the owning process.
            return new(InventoryReadStatus.Partial, result);
        }
        catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        { return new(InventoryReadStatus.Error, []); }
    }
}
