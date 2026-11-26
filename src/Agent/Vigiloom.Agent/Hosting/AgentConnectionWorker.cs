using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using Vigiloom.Agent.Core.Identity;

namespace Vigiloom.Agent.Hosting;

/// <summary>Receives work notifications; durable work remains on the server.</summary>
internal sealed class AgentConnectionWorker(IConfiguration configuration, AgentWorkNotifications notifications,
    TimeProvider clock, ILogger<AgentConnectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var directory = configuration["Vigiloom:Agent:StateDirectory"];
            var server = configuration["Vigiloom:Agent:Server"];
            if (!string.IsNullOrWhiteSpace(directory) && Uri.TryCreate(server, UriKind.Absolute, out var uri))
            {
                try
                {
                    var vault = new IdentityVault(directory);
                    var credential = await IdentityClient.ReadMachineCredentialAsync(vault, uri, stoppingToken);
                    if (credential is not null)
                    {
                        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        await using var connection = new HubConnectionBuilder()
                            .WithUrl(new Uri(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), "api/v1/agent/channel"), options =>
                            {
                                options.Transports = HttpTransportType.WebSockets;
                                options.SkipNegotiation = true;
                                options.AccessTokenProvider = () => Task.FromResult<string?>(credential);
                            })
                            .Build();
                        connection.On("UpdateAvailable", notifications.NotifyUpdate);
                        connection.On("ScanAvailable", notifications.NotifyScan);
                        connection.Closed += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
                        await connection.StartAsync(stoppingToken);
                        notifications.NotifyUpdate();
                        notifications.NotifyScan();
                        await disconnected.Task.WaitAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogWarning("Agent notification connection unavailable: {ErrorType}.", error.GetType().Name);
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
