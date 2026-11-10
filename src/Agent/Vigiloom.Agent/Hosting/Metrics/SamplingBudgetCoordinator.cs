namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Serializes Agent collection cycles so producers account against the same process budget.</summary>
internal sealed class SamplingBudgetCoordinator(TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public SamplingCpuBudget Cpu { get; } = new(clock, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3));
    public SamplingIoBudget Io { get; } = new(clock, TimeSpan.FromMinutes(1), 64UL * 1024 * 1024);

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? owned = gate;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref owned, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
