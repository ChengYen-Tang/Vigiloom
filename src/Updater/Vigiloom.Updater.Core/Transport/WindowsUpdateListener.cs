using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Vigiloom.BuildingBlocks.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Updater.Core.Transport;

/// <summary>Receives update requests through a pipe restricted to the configured Agent service identity.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateListener : ILocalUpdateListener
{
    private readonly string pipeName;
    private readonly PipeSecurity security;
    private readonly Channel<UpdateHandoff> requests = Channel.CreateBounded<UpdateHandoff>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource shutdown = new();
    private int started;

    public WindowsUpdateListener(SecurityIdentifier agentSid) : this(agentSid, UpdateHandoff.WindowsPipeName) { }

    internal WindowsUpdateListener(SecurityIdentifier agentSid, string pipeName)
    {
        ArgumentNullException.ThrowIfNull(agentSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.pipeName = pipeName;
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException("The Updater identity is unavailable.");
        security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        const PipeAccessRights client = PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes |
            PipeAccessRights.WriteAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(agentSid, client, AccessControlType.Allow));
    }

    public ChannelReader<UpdateHandoff> Requests => requests.Reader;

    public async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("The update listener is already running.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        try
        {
            // Keep the first instance for the entire lifetime so another process cannot
            // acquire the name between requests.
            await using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
            while (!cancellation.IsCancellationRequested)
            {
                var connected = false;
                try
                {
                    await pipe.WaitForConnectionAsync(cancellation.Token);
                    connected = true;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var request = await BoundedJsonFrame.ReadAsync<UpdateHandoff>(pipe, deadline.Token);
                    var status = !request.IsValid() ? UpdateHandoffStatus.Rejected :
                        requests.Writer.TryWrite(request) ? UpdateHandoffStatus.Accepted : UpdateHandoffStatus.Busy;
                    await BoundedJsonFrame.WriteAsync(pipe, new UpdateHandoffResponse { Status = status }, deadline.Token);
                    // Wait for the client to consume the reply and close; Disconnect can discard unread bytes.
                    if (await pipe.ReadAsync(new byte[1], deadline.Token) != 0)
                        throw new InvalidDataException("Unexpected data after the local update request.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or JsonException or OperationCanceledException)
                { /* Ignore disconnected or malformed clients and accept the next request. */ }
                finally { if (connected) pipe.Disconnect(); }
            }
        }
        finally { requests.Writer.TryComplete(); }
    }

    public ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        return ValueTask.CompletedTask;
    }
}
