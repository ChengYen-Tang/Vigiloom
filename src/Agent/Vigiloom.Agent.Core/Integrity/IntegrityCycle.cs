using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Core.Integrity;

public enum IntegrityCycleKind { Queued, Waiting, Complete, Rejected, Unavailable, OwnerChanged, QueueFull, TooLarge, PolicyChanged }
public sealed record IntegrityCycleResult(IntegrityCycleKind Kind, Guid ScopeId);
public sealed record IntegrityReadResult(IntegrityScanOutcome Outcome, IReadOnlyList<IntegrityItem> Items,
    bool CoverageGap, IntegrityPartialReason? PartialReason = null);

/// <summary>Sequences one scope generation; the caller holds the exclusive integrity producer lease.</summary>
public sealed class IntegrityCycle(IntegrityStatusClient statusClient, TimeProvider clock)
{
    /// <summary>Reconciles a previous generation even when no scope remains enabled.</summary>
    public async Task<IntegrityCycleResult?> ResumeOutstandingAsync(IdentityVault vault, Uri server, AgentSettingsOwner owner, CancellationToken token)
    {
        var queue = new DurableBatchQueue(vault, owner.AgentId);
        var pending = await queue.ReadOutstandingIntegrityAsync(owner, token);
        if (pending is null) return null;
        var observed = await statusClient.ReadAsync(vault, server, pending.ScopeId, token);
        if (observed.Kind != IntegrityStatusResultKind.Success || observed.Owner != owner)
            return new(observed.Kind == IntegrityStatusResultKind.OwnerChanged ? IntegrityCycleKind.OwnerChanged : IntegrityCycleKind.Unavailable, pending.ScopeId);
        var reconciled = await queue.ReconcileIntegrityAsync(owner, pending, observed, token);
        return new(reconciled switch
        {
            IntegrityReconcileResult.Pending => IntegrityCycleKind.Waiting,
            IntegrityReconcileResult.Completed => IntegrityCycleKind.Complete,
            IntegrityReconcileResult.Rejected => IntegrityCycleKind.Rejected,
            IntegrityReconcileResult.OwnerChanged or IntegrityReconcileResult.OutstandingChanged => IntegrityCycleKind.OwnerChanged,
            _ => throw new InvalidOperationException("Unknown integrity reconciliation result.")
        }, pending.ScopeId);
    }

    public async Task<IntegrityCycleResult> RunAsync(IdentityVault vault, Uri server, AgentSettingsOwner owner,
        AgentSavedIntegritySettings policy, IntegrityScope scope,
        Func<IntegrityScope, CancellationToken, Task<IntegrityReadResult>> collect, CancellationToken token,
        Func<Func<CancellationToken, Task<EnqueueResult>>, CancellationToken, Task<EnqueueResult>>? continueUnderBudget = null)
    {
        ArgumentNullException.ThrowIfNull(collect);
        if (await ResumeOutstandingAsync(vault, server, owner, token) is { } resumed) return resumed;
        var queue = new DurableBatchQueue(vault, owner.AgentId);
        if (policy.AgentId != owner.AgentId || policy.Server != owner.Server || !policy.Snapshot.IsValid() ||
            !policy.Snapshot.Scopes.Any(x => x.Id == scope.Id && x.Revision == scope.Revision && x.Enabled && x.SamePolicy(scope)))
            throw new ArgumentException("The scope must be enabled in the owner's saved policy.", nameof(scope));
        var status = await statusClient.ReadAsync(vault, server, scope.Id, token);
        if (status.Kind != IntegrityStatusResultKind.Success || status.Owner != owner || status.Status is null)
            return new(status.Kind == IntegrityStatusResultKind.OwnerChanged ? IntegrityCycleKind.OwnerChanged : IntegrityCycleKind.Unavailable, scope.Id);
        if (!status.Status.ScopePresent || status.Status.Enabled != true || status.Status.CurrentRevision != scope.Revision)
            return new(IntegrityCycleKind.PolicyChanged, scope.Id);
        if (status.Status.ActiveGeneration is not null || status.Status.OtherScopeActive) return new(IntegrityCycleKind.Waiting, scope.Id);
        var gapGeneration = await queue.ReadIntegrityGapAsync(owner, scope.Id, scope.Revision, token);
        var observedAt = clock.GetUtcNow();
        var read = await collect(scope, token);
        var after = await statusClient.ReadAsync(vault, server, scope.Id, token);
        if (after.Kind != IntegrityStatusResultKind.Success || after.Owner != owner || after.Status is null)
            return new(after.Kind == IntegrityStatusResultKind.OwnerChanged ? IntegrityCycleKind.OwnerChanged : IntegrityCycleKind.Unavailable, scope.Id);
        if (!after.Status.ScopePresent || after.Status.Enabled != true || after.Status.CurrentRevision != scope.Revision)
            return new(IntegrityCycleKind.PolicyChanged, scope.Id);
        if (after.Status.ActiveGeneration is not null || after.Status.OtherScopeActive) return new(IntegrityCycleKind.Waiting, scope.Id);
        var generation = await queue.ReserveIntegrityGenerationAsync(owner, scope.Id, after.Status.HighWaterGeneration, token);
        var now = clock.GetUtcNow();
        var snapshotId = Guid.CreateVersion7();
        EnqueueResult queued;
        try
        {
            queued = continueUnderBudget is null ? await EnqueueAsync(token) : await continueUnderBudget(EnqueueAsync, token);
        }
        catch (ArgumentException) { return new(IntegrityCycleKind.TooLarge, scope.Id); }
        return new(queued switch
        {
            EnqueueResult.Queued or EnqueueResult.AlreadyQueued => IntegrityCycleKind.Queued,
            EnqueueResult.Full => IntegrityCycleKind.QueueFull,
            EnqueueResult.ExceedsCapacity => IntegrityCycleKind.TooLarge,
            EnqueueResult.IntegrityPolicyChanged => IntegrityCycleKind.PolicyChanged,
            EnqueueResult.IntegrityOutstanding => IntegrityCycleKind.Waiting,
            EnqueueResult.OwnerChanged or EnqueueResult.ConflictingBatch => IntegrityCycleKind.OwnerChanged,
            _ => throw new InvalidOperationException("Unknown integrity queue result.")
        }, scope.Id);

        async Task<EnqueueResult> EnqueueAsync(CancellationToken inner)
        {
            var batches = IntegrityChunkBuilder.Build(scope.Id, scope.Revision, snapshotId, generation, observedAt, now,
                read.Outcome, read.Items, read.CoverageGap || gapGeneration is not null, read.PartialReason);
            return await queue.EnqueueIntegrityForOwnerAsync(batches, owner, scope.Id, scope.Revision, generation, snapshotId,
                policy.SaveToken, gapGeneration, inner);
        }
    }
}
