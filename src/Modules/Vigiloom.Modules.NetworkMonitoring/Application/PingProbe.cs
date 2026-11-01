using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Vigiloom.Contracts.Network;

namespace Vigiloom.Modules.NetworkMonitoring.Application;

public sealed record PingMeasurement(PingOutcome Outcome, string? Address, double? LatencyMilliseconds);
public interface IPingProbe
{
    Task<PingMeasurement> MeasureAsync(string target, int timeoutSeconds, CancellationToken token);
}

/// <summary>One DNS resolution and one ICMP request; no hidden retries or fabricated samples.</summary>
public sealed class SystemPingProbe : IPingProbe
{
    public async Task<PingMeasurement> MeasureAsync(string target, int timeoutSeconds, CancellationToken token)
    {
        IPAddress? address;
        if (!IPAddress.TryParse(target, out address))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                address = (await Dns.GetHostAddressesAsync(target, deadline.Token))
                    .Where(x => x.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ThenBy(x => Convert.ToHexString(x.GetAddressBytes()), StringComparer.Ordinal).FirstOrDefault();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(PingOutcome.DnsError, null, null); }
            catch (SocketException) { return new(PingOutcome.DnsError, null, null); }
            catch (ArgumentException) { return new(PingOutcome.DnsError, null, null); }
            if (address is null) return new(PingOutcome.DnsError, null, null);
        }
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(timeoutSeconds), buffer: [], cancellationToken: token);
            return reply.Status switch
            {
                IPStatus.Success => new(PingOutcome.Success, address.ToString(), reply.RoundtripTime),
                IPStatus.TimedOut => new(PingOutcome.Timeout, address.ToString(), null),
                IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationHostUnreachable or IPStatus.DestinationUnreachable or
                    IPStatus.DestinationPortUnreachable or IPStatus.DestinationProtocolUnreachable => new(PingOutcome.Unreachable, address.ToString(), null),
                _ => new(PingOutcome.MonitorError, address.ToString(), null)
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is PingException or SocketException or PlatformNotSupportedException or UnauthorizedAccessException or Win32Exception)
        {
            var cause = exception.GetBaseException();
            var denied = cause is UnauthorizedAccessException or SocketException { SocketErrorCode: SocketError.AccessDenied } or Win32Exception { NativeErrorCode: 1 or 5 or 13 };
            return new(denied ? PingOutcome.PermissionDenied : PingOutcome.MonitorError, address.ToString(), null);
        }
    }
}
