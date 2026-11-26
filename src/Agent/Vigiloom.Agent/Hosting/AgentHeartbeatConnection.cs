using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting;

internal sealed class AgentHeartbeatConnection : IAgentHeartbeatTransport
{
    private readonly Lock gate = new();
    private HubConnection? connection;
    private Uri? server;
    private string? connectedCredential;

    public void Set(HubConnection value, Uri address, string credential)
    {
        lock (gate) { connection = value; server = address; connectedCredential = credential; }
    }

    public void Clear(HubConnection value)
    {
        lock (gate) { if (connection == value) { connection = null; server = null; connectedCredential = null; } }
    }

    public async Task DisconnectAsync(CancellationToken token)
    {
        HubConnection? current;
        lock (gate) current = connection;
        if (current is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await current.StopAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (InvalidOperationException) { }
    }

    public async Task<HttpStatusCode?> SendAsync(Uri address, string credential, AgentHeartbeatReport? report, CancellationToken token)
    {
        HubConnection? current;
        bool matches;
        lock (gate)
        {
            current = connection;
            matches = server == address && string.Equals(connectedCredential, credential, StringComparison.Ordinal);
        }
        if (current?.State != HubConnectionState.Connected) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (!matches)
            {
                await current.StopAsync(timeout.Token);
                return null;
            }
            return (HttpStatusCode)await current.InvokeAsync<int>("Heartbeat", credential, report, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is InvalidOperationException or IOException or Microsoft.AspNetCore.SignalR.HubException) { return null; }
    }
}
