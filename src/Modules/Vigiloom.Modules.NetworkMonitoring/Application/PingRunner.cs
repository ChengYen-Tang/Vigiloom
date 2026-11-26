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
        const int claimSize = 8;
        var running = new List<Task>(current.MaxConcurrency);
        try
        {
            while (true)
            {
                var available = current.MaxConcurrency - running.Count;
                if (available > 0)
                {
                    var leases = await store.ClaimAsync(current, Math.Min(claimSize, available), token);
                    foreach (var lease in leases) running.Add(MeasureAsync(lease));
                    if (leases.Count > 0) continue;
                }
                if (running.Count == 0) return;
                var completed = await Task.WhenAny(running);
                running.Remove(completed);
                await completed;
            }
        }
        finally
        {
            await Task.WhenAll(running);
        }

        async Task MeasureAsync(PingLease lease)
        {
            if (!await store.IsCurrentAsync(lease, token)) { await store.DeactivateAsync(lease, token); return; }
            var measuredAt = clock.GetUtcNow();
            var result = await probe.MeasureAsync(lease.Target, current.TimeoutSeconds, token);
            await store.CommitAsync(lease, current, result, measuredAt, "central", token);
        }
    }
}
