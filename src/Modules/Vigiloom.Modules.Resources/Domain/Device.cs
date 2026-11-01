using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.Resources.Domain;

public sealed class Device
{
    private Device() { }

    public Device(string name, string address, bool hasAgent, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        Id = ResourceIds.Create();
        Name = name;
        Address = address;
        HasAgent = hasAgent;
        CreatedAt = UpdatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Address { get; private set; } = null!;
    public bool HasAgent { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public void Rename(string name, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        UpdatedAt = now.ToUniversalTime();
    }
}
