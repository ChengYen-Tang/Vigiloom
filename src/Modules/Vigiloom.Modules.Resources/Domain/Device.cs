using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Modules.Resources.Domain;

public sealed class Device
{
    private Device() { }

    public Device(string name, string address, bool deepMonitoring, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        Id = ResourceIds.Create();
        Name = name;
        Address = address;
        DeepMonitoring = deepMonitoring;
        IsEnabled = true;
        CreatedAt = UpdatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Address { get; private set; } = null!;
    public bool DeepMonitoring { get; private set; }
    public bool IsEnabled { get; private set; }
    public bool IsArchived { get; private set; }
    public bool IsPublic { get; private set; }
    public string? PublicDisplayName { get; private set; }
    public string? PublicPolicyVersion { get; private set; }
    /// <summary>Changes on archive so restored devices cannot reactivate previous Agent credentials.</summary>
    public long AgentAuthorizationGeneration { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public void Rename(string name, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        UpdatedAt = now.ToUniversalTime();
    }

    public void Update(string name, string address, bool deepMonitoring, bool enabled, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (IsArchived) throw new InvalidOperationException("Archived devices cannot be edited.");
        Name = name;
        Address = address;
        DeepMonitoring = deepMonitoring;
        IsEnabled = enabled;
        UpdatedAt = now.ToUniversalTime();
    }

    public void Archive(DateTimeOffset now)
    {
        if (IsArchived) return;
        AgentAuthorizationGeneration = checked(AgentAuthorizationGeneration + 1);
        IsArchived = true;
        IsEnabled = false;
        IsPublic = false;
        UpdatedAt = now.ToUniversalTime();
    }

    public void SetPublication(bool isPublic, string? displayName, string? policyVersion, DateTimeOffset now)
    {
        if (IsArchived) throw new InvalidOperationException("Archived devices cannot be published.");
        if (isPublic && (policyVersion != PublicationPolicy.BaselineVersion || !PublicationPolicy.TryValidateName(displayName, out _)))
            throw new ArgumentException("An approved publication policy and public name are required.");
        IsPublic = isPublic;
        if (isPublic) { PublicDisplayName = displayName; PublicPolicyVersion = policyVersion; }
        UpdatedAt = now.ToUniversalTime();
    }

    public void Restore(DateTimeOffset now)
    {
        if (!IsArchived) return;
        IsArchived = false;
        IsEnabled = false;
        UpdatedAt = now.ToUniversalTime();
    }
}
