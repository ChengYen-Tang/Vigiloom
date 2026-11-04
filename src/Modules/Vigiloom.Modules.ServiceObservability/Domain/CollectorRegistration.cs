namespace Vigiloom.Modules.ServiceObservability.Domain;

/// <summary>An administrator-identified Collector and its dedicated monitoring service.</summary>
public sealed class CollectorRegistration
{
    private CollectorRegistration() { }

    public CollectorRegistration(string name, Guid monitoringServiceId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new ArgumentException("A name of at most 128 characters is required.", nameof(name));
        if (monitoringServiceId.Version != 7) throw new ArgumentException("A monitoring service is required.", nameof(monitoringServiceId));
        Id = Guid.CreateVersion7(); Name = name.Trim(); MonitoringServiceId = monitoringServiceId;
        CreatedAt = UpdatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public string Name { get; private set; } = "";
    public Guid MonitoringServiceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];
    public void Touch(DateTimeOffset now) => UpdatedAt = now.ToUniversalTime();
}
