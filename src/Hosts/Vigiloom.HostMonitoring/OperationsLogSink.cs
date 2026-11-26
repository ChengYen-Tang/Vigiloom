using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal sealed class OperationsLogSink(HttpClient client, ILogger<OperationsLogSink> logger) : IHostLogSink
{
    public async Task<LogReceipt> ReceiveAsync(RegisteredAgentIdentity identity, AgentLogBatch batch, CancellationToken token)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("internal/v1/host-logs",
                new HostLogSubmission(identity.AgentId, identity.DeviceId, identity.DeviceGeneration, batch), token);
            var receipt = await response.Content.ReadFromJsonAsync<LogReceipt>(cancellationToken: token);
            if (receipt is null || !Enum.IsDefined(receipt.Status) || response.IsSuccessStatusCode != (receipt.Status == LogReceiptStatus.Accepted))
                return new(LogReceiptStatus.Busy);
            return receipt;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning("Host Log delivery failed: {ErrorType}.", error.GetType().Name);
            return new(LogReceiptStatus.Busy);
        }
    }
}
