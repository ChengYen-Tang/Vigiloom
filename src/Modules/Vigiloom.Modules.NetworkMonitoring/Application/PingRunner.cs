using Vigiloom.Contracts.Network;

namespace Vigiloom.Modules.NetworkMonitoring.Application;

/// <summary>A bounded scan; the SQL store arbitrates leases across processes.</summary>
public sealed class PingRunner(PingSettings settings, PingStore store, IPingProbe probe, TimeProvider clock)
{
    public async Task RunOnceAsync(CancellationToken token)
    {
        await settings.EnsureAsync(token);
        var current = await settings.GetAsync(token);
        await store.ReconcileAsync(current, token);
        var leases = await store.ClaimAsync(current, token);
        await Parallel.ForEachAsync(leases, new ParallelOptions { MaxDegreeOfParallelism = current.MaxConcurrency, CancellationToken = token },
            async (lease, cancellationToken) =>
            {
                if (!await store.IsCurrentAsync(lease, cancellationToken)) { await store.DeactivateAsync(lease, cancellationToken); return; }
                var measuredAt = clock.GetUtcNow();
                var result = await probe.MeasureAsync(lease.Target, current.TimeoutSeconds, cancellationToken);
                await store.CommitAsync(lease, current, result, measuredAt, "central", cancellationToken);
            });
    }
}
