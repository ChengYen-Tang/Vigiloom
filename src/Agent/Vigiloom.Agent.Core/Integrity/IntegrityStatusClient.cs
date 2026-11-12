using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Core.Integrity;

public enum IntegrityStatusResultKind { Success, NotRegistered, ServerMismatch, Unauthorized, Unavailable, Invalid, OwnerChanged }
public sealed record IntegrityStatusResult(IntegrityStatusResultKind Kind, IntegrityScopeStatus? Status = null, AgentSettingsOwner? Owner = null);

/// <summary>Reads one authenticated scope's central generation fence before collecting or reconciling.</summary>
public sealed class IntegrityStatusClient(HttpClient http, TimeProvider clock)
{
    public async Task<IntegrityStatusResult> ReadAsync(IdentityVault vault, Uri server, Guid scopeId, CancellationToken token)
    {
        if (scopeId.Version != 7) throw new ArgumentException("A version 7 scope ID is required.", nameof(scopeId));
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? IntegrityStatusResultKind.ServerMismatch : IntegrityStatusResultKind.NotRegistered);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(expected, $"api/v1/agent/integrity-status/{scopeId:D}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{identity.AgentId!.Value:D}.{identity.MachineSecret}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(IntegrityStatusResultKind.Unauthorized);
            if (response.StatusCode != HttpStatusCode.OK) return new(IntegrityStatusResultKind.Unavailable);
            await response.Content.LoadIntoBufferAsync(8192, linked.Token);
            var status = await response.Content.ReadFromJsonAsync<IntegrityScopeStatus>(cancellationToken: linked.Token);
            if (!Valid(status, scopeId)) return new(IntegrityStatusResultKind.Invalid);
            var (current, _) = await IdentityClient.ReadRegisteredAsync(vault, expected, linked.Token);
            if (current?.AgentId != identity.AgentId) return new(IntegrityStatusResultKind.OwnerChanged);
            return new(IntegrityStatusResultKind.Success, status,
                new AgentSettingsOwner(identity.AgentId!.Value, expected.AbsoluteUri, vault.StateDirectory));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(IntegrityStatusResultKind.Unavailable); }
        catch (HttpRequestException) { return new(IntegrityStatusResultKind.Unavailable); }
        catch (JsonException) { return new(IntegrityStatusResultKind.Invalid); }
    }

    private static bool Valid(IntegrityScopeStatus? status, Guid scopeId)
    {
        if (status is null || status.ScopeId != scopeId || status.HighWaterGeneration < 0 ||
            (status.ScopePresent ? status.CurrentRevision is null || status.Enabled is null : status.CurrentRevision is not null || status.Enabled is not null) || status.CurrentRevision is <= 0 ||
            status.ActiveGeneration is <= 0 || status.LastAttemptGeneration is <= 0 ||
            status.ActiveGeneration > status.HighWaterGeneration || status.LastAttemptGeneration > status.HighWaterGeneration ||
            status.ActiveGeneration is { } active && (active != status.HighWaterGeneration || status.LastAttemptGeneration >= active) ||
            (status.LastAttemptGeneration is null) != (status.LastOutcome is null) ||
            (status.LastAttemptGeneration is null) != (status.LastStatus is null) ||
            (status.LastAttemptGeneration is null) != (status.LastSnapshotId is null) ||
            (status.LastAttemptGeneration is null) != (status.LastCoverageGap is null) ||
            status.LastSnapshotId is { Version: not 7 } ||
            status.LastOutcome is { } outcome && !Enum.IsDefined(outcome) ||
            status.LastStatus is { } completion && !Enum.IsDefined(completion) ||
            status.LastStatus == IntegrityCompletion.Complete && status.LastOutcome != IntegrityScanOutcome.Complete ||
            status.LastStatus == IntegrityCompletion.Partial && status.LastOutcome == IntegrityScanOutcome.Complete) return false;
        if (status.LastPartialReason is { } reason && (status.LastOutcome != IntegrityScanOutcome.Partial || reason == 0 ||
            (reason & ~(IntegrityPartialReason.AccessDenied | IntegrityPartialReason.LimitReached | IntegrityPartialReason.ChangedDuringScan |
                IntegrityPartialReason.SourceUnavailable | IntegrityPartialReason.Other)) != 0)) return false;
        return status.LastOutcome != IntegrityScanOutcome.Partial || status.LastPartialReason is not null;
    }
}
