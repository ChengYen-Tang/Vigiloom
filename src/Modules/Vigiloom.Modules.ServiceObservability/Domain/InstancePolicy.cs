namespace Vigiloom.Modules.ServiceObservability.Domain;

/// <summary>The administrator-controlled central resource admission policy.</summary>
public sealed class InstancePolicy
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public long RowId { get; private set; }
    public string Name { get; set; } = "central";
    public int PerService { get; set; } = 1_000;
    public int Total { get; set; } = 100_000;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public byte[] Version { get; private set; } = [];
}
