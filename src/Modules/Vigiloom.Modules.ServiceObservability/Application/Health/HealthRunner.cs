using System.Net;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Runs a bounded group of centrally leased application health checks.</summary>
public sealed class HealthRunner(IDbContextFactory<ServiceObservabilityDbContext> contexts, TimeProvider clock) : IDisposable
{
    private const int Concurrency = 16;
    private readonly HealthStore store = new(contexts);
    private readonly HealthTransportPool transport = new();
    private readonly SemaphoreSlim policyGate = new(1, 1);
    private Guid? policyId;
    private long policyGeneration, transportGeneration;
    private byte[]? policyVersion;

    public async Task RunOnceAsync(CancellationToken token)
    {
        HealthNetworkSnapshot? network;
        network = await RefreshPolicyAsync(token);
        if (network is null) return;
        var leases = await store.ClaimAsync(Concurrency, token);
        await Parallel.ForEachAsync(leases, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = token },
            async (lease, cancellationToken) =>
            {
                var work = await store.LoadAsync(lease, cancellationToken);
                if (work is null || !Uri.TryCreate(work.Endpoint, UriKind.Absolute, out var endpoint))
                {
                    await store.DeferAsync(lease, 60, cancellationToken);
                    return;
                }
                if (!await store.IsNetworkCurrentAsync(network, cancellationToken))
                {
                    await store.DeferAsync(lease, 1, cancellationToken);
                    return;
                }
                var databaseStart = await store.NowAsync(cancellationToken);
                var monotonicStart = clock.GetTimestamp();
                var result = await transport.ReadAsync(network.TransportGeneration, endpoint, TimeSpan.FromSeconds(work.TimeoutSeconds), cancellationToken);
                var observedAt = databaseStart + clock.GetElapsedTime(monotonicStart);
                if (result.Result.Failure == HealthProbeFailure.PolicyChanged)
                {
                    await store.DeferAsync(lease, 1, cancellationToken);
                    return;
                }
                var kind = result.Result switch
                {
                    { Health: ReportedHealth.Healthy } => HealthObservationKind.Healthy,
                    { Health: ReportedHealth.Degraded } => HealthObservationKind.Degraded,
                    { Health: ReportedHealth.Unhealthy } => HealthObservationKind.Unhealthy,
                    { Failure: HealthProbeFailure.Timeout } => HealthObservationKind.Timeout,
                    { Failure: HealthProbeFailure.Transport } => HealthObservationKind.TransportError,
                    { Failure: HealthProbeFailure.NetworkDenied } => HealthObservationKind.NetworkDenied,
                    _ => HealthObservationKind.InvalidResponse
                };
                if (!await store.CommitAsync(work, network, kind, observedAt, cancellationToken))
                    await store.DeferAsync(lease, 5, cancellationToken);
            });
    }

    /// <summary>Refreshes allowed networks independently of active health requests.</summary>
    public async Task RefreshNetworkPolicyAsync(CancellationToken token) => await RefreshPolicyAsync(token);

    /// <summary>Closes stale state intervals even when no fresh probe arrives.</summary>
    public Task ExpireStaleAsync(CancellationToken token) => store.ExpireDueAsync(token);

    private async Task<HealthNetworkSnapshot?> RefreshPolicyAsync(CancellationToken token)
    {
        await policyGate.WaitAsync(token);
        try
        {
            HealthNetworkSnapshot? network;
            try { network = await store.NetworkAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { InvalidatePolicy(); throw; }
            if (network is null || network.Networks.Count == 0)
            {
                InvalidatePolicy();
                return null;
            }
            if (policyId == network.Id && network.Generation < policyGeneration)
            {
                InvalidatePolicy();
                return null;
            }
            if (policyId != network.Id || policyGeneration != network.Generation ||
                policyVersion is null || !policyVersion.AsSpan().SequenceEqual(network.Version))
            {
                transport.ApplyPolicy(checked(++transportGeneration), network.Networks.Select(IPNetwork.Parse).ToArray());
                policyId = network.Id;
                policyGeneration = network.Generation;
                policyVersion = network.Version;
            }
            return network with { TransportGeneration = transportGeneration };
        }
        finally { policyGate.Release(); }
    }

    private void InvalidatePolicy()
    {
        if (policyId is null) return;
        transport.ApplyPolicy(checked(++transportGeneration), []);
        policyId = null;
        policyGeneration = 0;
        policyVersion = null;
    }

    public void Dispose()
    {
        transport.Dispose();
        policyGate.Dispose();
    }
}
