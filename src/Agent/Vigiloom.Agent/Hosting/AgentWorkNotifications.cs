namespace Vigiloom.Agent.Hosting;

/// <summary>Coalesces work notifications while preserving the periodic polling fallback.</summary>
internal sealed class AgentWorkNotifications : IDisposable
{
    private readonly SemaphoreSlim updates = new(0, 1);
    private readonly SemaphoreSlim scans = new(0, 1);

    public void NotifyUpdate() => Signal(updates);
    public void NotifyScan() => Signal(scans);

    public Task WaitForUpdateAsync(TimeSpan delay, TimeProvider clock, CancellationToken token) => WaitAsync(updates, delay, clock, token);
    public Task WaitForScanAsync(TimeSpan delay, TimeProvider clock, CancellationToken token) => WaitAsync(scans, delay, clock, token);

    private static void Signal(SemaphoreSlim semaphore)
    {
        try { semaphore.Release(); }
        catch (SemaphoreFullException) { }
    }

    private static async Task WaitAsync(SemaphoreSlim semaphore, TimeSpan delay, TimeProvider clock, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(delay, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { await semaphore.WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        updates.Dispose();
        scans.Dispose();
    }
}
