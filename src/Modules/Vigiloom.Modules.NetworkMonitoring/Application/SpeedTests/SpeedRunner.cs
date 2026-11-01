using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

public sealed class SpeedRunner(SpeedQueue queue, ISpeedProbe probe, ILogger<SpeedRunner> logger)
{
    public async Task RunOnceAsync(CancellationToken token)
    {
        var lease = await queue.ClaimAsync(token);
        if (lease is null) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var measurement = probe.MeasureAsync(lease, stop.Token);
        try
        {
            while (!measurement.IsCompleted)
            {
                await Task.WhenAny(measurement, Task.Delay(500, stop.Token));
                if (measurement.IsCompleted) break;
                if (stop.IsCancellationRequested || !await queue.MayContinueAsync(lease, token)) { await stop.CancelAsync(); break; }
            }
            var result = await measurement;
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await queue.CompleteAsync(lease, result, finish.Token);
        }
        catch
        {
            await stop.CancelAsync();
            try { await measurement; } catch (Exception exception) { logger.LogWarning("Speed test stopped: {ExceptionType}", exception.GetType().Name); }
            throw;
        }
    }
}
