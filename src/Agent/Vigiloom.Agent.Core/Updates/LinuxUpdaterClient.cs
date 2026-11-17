using System.Net.Sockets;
using System.Runtime.Versioning;
using Vigiloom.BuildingBlocks.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Updates;

/// <summary>Hands an update request to a local, root-owned Updater service.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxUpdaterClient
{
    public static async Task<UpdateHandoffStatus> SubmitAsync(string socketPath, UpdateHandoff request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(socketPath) || !request.IsValid())
            throw new ArgumentException("An absolute socket path and a valid update request are required.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
        if (LinuxPeerCredentials.Read(socket).UserId != 0)
            throw new UnauthorizedAccessException("The local Updater does not run as root.");
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await BoundedJsonFrame.WriteAsync(stream, request, timeout.Token);
        var response = await BoundedJsonFrame.ReadAsync<UpdateHandoffResponse>(stream, timeout.Token);
        if (!response.IsValid()) throw new InvalidDataException("The local Updater response is invalid.");
        return response.Status;
    }
}
