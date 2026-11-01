using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.Resources.Domain;

public sealed class Group
{
    private Group() { }
    public Group(string name, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = ResourceIds.Create();
        Name = name;
        CreatedAt = UpdatedAt = now.ToUniversalTime();
    }
    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public string Name { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];
}
