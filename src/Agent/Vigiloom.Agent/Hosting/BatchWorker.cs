using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent;
using Vigiloom.Agent.Core.Transport;

namespace Vigiloom.Agent.Hosting;

/// <summary>Drains durable batches without overlapping requests, retaining all unconfirmed data.</summary>
internal sealed class BatchWorker(AgentBatchClient client, AgentSettingsRuntime settings, IConfiguration configuration, TimeProvider clock, ILogger<BatchWorker> logger) : BackgroundService
{
    private const int MaximumCatchUpBurst = 4;
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay;
        try { delay = await NormalDelayAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        var failures = 0;
        var consecutiveDelivered = 0;
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
            catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or OperationCanceledException or System.Data.Common.DbException)
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
            if (outcome.Status is BatchSendResult.Throttled)
            {
                failures = 0;
                consecutiveDelivered = 0;
                delay = outcome.RetryAfter is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
            }
            else if (outcome.Status is BatchSendResult.Retry)
            {
                consecutiveDelivered = 0;
                failures = Math.Min(failures + 1, 7);
                var seconds = Math.Min(300, 5 * (1 << (failures - 1)));
                delay = TimeSpan.FromSeconds(Math.Min(300, seconds * (1 + Random.Shared.NextDouble() * 0.2)));
                if (outcome.RetryAfter is { } requested && requested > delay) delay = requested;
            }
            else
            {
                failures = 0;
                if (outcome.Status == BatchSendResult.Delivered && ++consecutiveDelivered < MaximumCatchUpBurst)
                {
                    // Probe the next persisted batch promptly, but periodically yield to
                    // the normal interval so a backlog cannot monopolize the receiver.
                    delay = CatchUpDelay;
                    continue;
                }
                consecutiveDelivered = 0;
                try
                {
                    delay = outcome.Status is BatchSendResult.NotRegistered or BatchSendResult.ServerMismatch or BatchSendResult.Unauthorized
                        ? TimeSpan.FromSeconds(30)
                        : await NormalDelayAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private TimeSpan NormalDelay() => TimeSpan.FromSeconds(settings.Current.BatchIntervalSeconds * (1 + Random.Shared.NextDouble() * 0.2));

    private async Task<TimeSpan> NormalDelayAsync(CancellationToken token)
    {
        var directory = configuration["Vigiloom:Agent:StateDirectory"];
        var server = configuration["Vigiloom:Agent:Server"];
        if (string.IsNullOrWhiteSpace(directory) || !Uri.TryCreate(server, UriKind.Absolute, out var uri)) settings.Bind(null);
        else
        {
            try
            {
                var owner = await AgentSettingsClient.GetOwnerAsync(new IdentityVault(directory), uri, token);
                if (!string.Equals(directory, configuration["Vigiloom:Agent:StateDirectory"], StringComparison.Ordinal) ||
                    !string.Equals(server, configuration["Vigiloom:Agent:Server"], StringComparison.Ordinal)) settings.Bind(null);
                else settings.Bind(owner);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // An unavailable identity cannot authorize cached settings, but is not
                // evidence that the previous owner changed. Keep last-good for recovery.
                return TimeSpan.FromSeconds(AgentSettings.Default.BatchIntervalSeconds * (1 + Random.Shared.NextDouble() * 0.2));
            }
        }
        return NormalDelay();
    }

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
