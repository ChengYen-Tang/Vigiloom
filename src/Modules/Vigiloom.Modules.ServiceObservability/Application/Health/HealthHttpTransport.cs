using System.Net;
using System.Net.Sockets;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

internal sealed class HealthNetworkDeniedException() : HttpRequestException("No resolved address is allowed by the health network policy.");

/// <summary>Connects only to addresses within the explicitly configured monitoring networks.</summary>
internal static class HealthHttpTransport
{
    internal static SocketsHttpHandler CreateHandler(IReadOnlyCollection<IPNetwork> allowedNetworks)
    {
        ArgumentNullException.ThrowIfNull(allowedNetworks);
        var networks = allowedNetworks.ToArray();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, token) => ConnectAsync(context, networks, token)
        };
    }

    internal static bool IsAllowed(IPAddress address, IReadOnlyCollection<IPNetwork> networks)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return networks.Any(network => network.Contains(address));
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context,
        IReadOnlyCollection<IPNetwork> networks, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        var allowed = false;
        foreach (var address in addresses)
        {
            if (!IsAllowed(address, networks)) continue;
            allowed = true;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        if (!allowed) throw new HealthNetworkDeniedException();
        throw new HttpRequestException("No allowed health endpoint address is reachable.");
    }
}
