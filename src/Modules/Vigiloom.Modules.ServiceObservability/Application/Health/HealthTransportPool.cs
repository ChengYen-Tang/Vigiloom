using System.Net;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

internal readonly record struct VersionedHealthProbeResult(long PolicyVersion, HealthProbeResult Result);

/// <summary>Owns a reusable HTTP transport for one network policy generation.</summary>
internal sealed class HealthTransportPool : IDisposable
{
    private readonly Lock gate = new();
    private Entry? current;
    private bool disposed;

    internal void ApplyPolicy(long version, IReadOnlyCollection<IPNetwork> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        Entry? previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (version <= 0 || (current is not null && version <= current.Version))
                throw new ArgumentOutOfRangeException(nameof(version));
            var client = new HttpClient(HealthHttpTransport.CreateHandler(networks)) { Timeout = Timeout.InfiniteTimeSpan };
            previous = current;
            current = new Entry(version, client, new HealthProbe(client), new CancellationTokenSource());
        }
        Retire(previous);
    }

    internal async Task<VersionedHealthProbeResult> ReadAsync(long expectedVersion, Uri endpoint, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Entry entry;
        CancellationToken policyToken;
        CancellationTokenSource linked;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            entry = current ?? throw new InvalidOperationException("A network policy must be configured first.");
            if (entry.Version != expectedVersion)
                return new(expectedVersion, new(null, HealthProbeFailure.PolicyChanged));
            policyToken = entry.Revoked.Token;
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, policyToken);
        }
        using (linked)
        {
            try
            {
                var result = await entry.Probe.ReadAsync(endpoint, timeout, linked.Token);
                token.ThrowIfCancellationRequested();
                if (policyToken.IsCancellationRequested)
                    return new(entry.Version, new(null, HealthProbeFailure.PolicyChanged));
                return new(entry.Version, result);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException &&
                policyToken.IsCancellationRequested && !token.IsCancellationRequested)
            {
                return new(entry.Version, new(null, HealthProbeFailure.PolicyChanged));
            }
        }
    }

    public void Dispose()
    {
        Entry? previous;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            previous = current;
            current = null;
        }
        Retire(previous);
    }

    private static void Retire(Entry? entry)
    {
        if (entry is null) return;
        entry.Revoked.Cancel();
        entry.Client.Dispose();
        entry.Revoked.Dispose();
    }

    private sealed record Entry(long Version, HttpClient Client, HealthProbe Probe, CancellationTokenSource Revoked);
}
