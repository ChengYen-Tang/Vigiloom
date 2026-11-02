using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;

namespace Vigiloom.Agent.Hosting;

/// <summary>Drains durable batches without overlapping requests, retaining all unconfirmed data.</summary>
internal sealed class BatchWorker(AgentBatchClient client, IConfiguration configuration, TimeProvider clock, ILogger<BatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = NormalDelay();
        var failures = 0;
        string? previous = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DelayAsync(delay, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }

            BatchSendOutcome outcome;
            string? failureType = null;
            try
            {
                var directory = configuration["Vigiloom:Agent:StateDirectory"];
                var server = configuration["Vigiloom:Agent:Server"];
                outcome = string.IsNullOrWhiteSpace(directory) || !Uri.TryCreate(server, UriKind.Absolute, out var uri)
                    ? new(BatchSendResult.NotRegistered)
                    : await client.SendNextAsync(new IdentityVault(directory), uri, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or OperationCanceledException or System.Data.Common.DbException)
            {
                outcome = new(BatchSendResult.Retry);
                failureType = ex.GetType().Name;
            }

            var status = failureType is null ? outcome.Status.ToString() : $"{outcome.Status} ({failureType})";
            if (!string.Equals(previous, status, StringComparison.Ordinal))
            {
                if (outcome.Status is BatchSendResult.Delivered or BatchSendResult.NoPendingBatch)
                    logger.LogInformation("Agent batch transport: {Status}.", status);
                else logger.LogWarning("Agent batch transport: {Status}.", status);
                previous = status;
            }
            if (outcome.Status is BatchSendResult.Retry)
            {
                failures = Math.Min(failures + 1, 7);
                var seconds = Math.Min(300, 5 * (1 << (failures - 1)));
                delay = TimeSpan.FromSeconds(Math.Min(300, seconds * (1 + Random.Shared.NextDouble() * 0.2)));
                if (outcome.RetryAfter is { } requested && requested > delay) delay = requested;
            }
            else
            {
                failures = 0;
                delay = outcome.Status is BatchSendResult.NotRegistered or BatchSendResult.ServerMismatch or BatchSendResult.Unauthorized
                    ? TimeSpan.FromSeconds(30)
                    : NormalDelay();
            }
        }
    }

    private static TimeSpan NormalDelay() => TimeSpan.FromSeconds(5 + Random.Shared.NextDouble());

    private static async Task DelayAsync(TimeSpan delay, TimeProvider clock, CancellationToken token)
    {
        // Very large Retry-After values exceed the maximum supported by a single timer.
        var maximumSlice = TimeSpan.FromDays(1);
        while (delay > maximumSlice)
        {
            await Task.Delay(maximumSlice, clock, token);
            delay -= maximumSlice;
        }
        await Task.Delay(delay, clock, token);
    }
}
