using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Modules.Resources.Domain;

public sealed class Group
{
    private Group() { }
    public Group(string name, DateTimeOffset now) : this(name, null, now) { }
    public Group(string name, string? description, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = ResourceIds.Create();
        Name = name;
        Description = description;
        CreatedAt = UpdatedAt = now.ToUniversalTime();
    }
    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public bool IsPublic { get; private set; }
    public string? PublicDisplayName { get; private set; }
    public string? PublicPolicyVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public void Update(string name, string? description, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description;
        Touch(now);
    }

    public void Touch(DateTimeOffset now) => UpdatedAt = now.ToUniversalTime();

    public void SetPublication(bool isPublic, string? displayName, string? policyVersion, DateTimeOffset now)
    {
        if (isPublic && (policyVersion != PublicationPolicy.BaselineVersion || !PublicationPolicy.TryValidateName(displayName, out _)))
            throw new ArgumentException("An approved publication policy and public name are required.");
        IsPublic = isPublic;
        if (isPublic) { PublicDisplayName = displayName; PublicPolicyVersion = policyVersion; }
        Touch(now);
    }
}
