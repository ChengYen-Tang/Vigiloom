using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Baseline;

/// <summary>Last-good baseline policy bound to the active Agent identity.</summary>
internal sealed class AgentBaselineSettingsRuntime
{
    private readonly object gate = new();
    private AgentSettingsOwner? owner;
    private AgentSavedBaselineSettings? saved;
    private AgentConfigurationFailure? failure;
    private bool unsupported;
    private string? appliedRevision;
    private DateTimeOffset? appliedAt;
    private DateTimeOffset? lastSyncedAt;

    public void Bind(AgentSettingsOwner? value)
    {
        lock (gate)
        {
            if (owner == value) return;
            owner = value;
            saved = null;
            failure = null;
            unsupported = false;
            appliedRevision = null;
            appliedAt = null;
            lastSyncedAt = null;
        }
    }

    public bool NeedsRestore(AgentSettingsOwner value) { lock (gate) return owner == value && saved is null; }

    public void RestoreIfEmpty(AgentSettingsOwner? value, AgentSavedBaselineSettings policy)
    {
        lock (gate) if (saved is null && Matches(value, policy)) saved = policy;
    }

    public void Publish(AgentSettingsOwner? value, AgentSavedBaselineSettings policy, DateTimeOffset? syncedAt = null)
    {
        lock (gate)
        {
            if (!Matches(value, policy)) return;
            saved = policy;
            lastSyncedAt = syncedAt?.ToUniversalTime() ?? lastSyncedAt;
            failure = null;
            unsupported = false;
        }
    }

    public void MarkFailure(AgentSettingsOwner? value, AgentConfigurationFailure reason)
    {
        lock (gate) if (owner == value) failure = failure == AgentConfigurationFailure.Unauthorized ? failure : reason;
    }

    public AgentSavedBaselineSettings? ForOwner(AgentSettingsOwner value)
    {
        lock (gate) return owner == value && !unsupported && failure != AgentConfigurationFailure.Unauthorized ? saved : null;
    }

    public AgentSavedBaselineSettings? ApplyForOwner(AgentSettingsOwner value, DateTimeOffset now)
    {
        lock (gate)
        {
            if (owner != value || unsupported || failure == AgentConfigurationFailure.Unauthorized || saved is null) return null;
            if (appliedRevision != saved.Snapshot.Revision)
            {
                appliedRevision = saved.Snapshot.Revision;
                appliedAt = now.ToUniversalTime();
            }
            return saved;
        }
    }

    public (AgentSettingsOwner Owner, BaselineSettingsReport Report)? Report(AgentSettingsOwner? value)
    {
        lock (gate) return value is not null && owner == value
            ? (value, new(appliedRevision, appliedAt, lastSyncedAt, failure)) : null;
    }

    public void MarkUnsupported(AgentSettingsOwner? value)
    {
        lock (gate) if (owner == value) { unsupported = true; failure = AgentConfigurationFailure.Unavailable; }
    }

    private bool Matches(AgentSettingsOwner? value, AgentSavedBaselineSettings policy) => value is not null && owner == value &&
        policy.AgentId == value.AgentId && string.Equals(policy.Server, value.Server, StringComparison.Ordinal);
}
