using Vigiloom.BuildingBlocks.Identifiers;

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
        IsArchived = true;
        IsEnabled = false;
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
