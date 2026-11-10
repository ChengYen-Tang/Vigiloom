using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>A persistent per-device source identity. Retirement preserves identity and prevents reuse.</summary>
public sealed class HostLogSource
{
    private HostLogSource() { }

    public HostLogSource(Guid deviceId, AgentLogSourceKind kind, string name, string target, bool enabled,
        AgentTextLogMode? textMode, Guid actorId, DateTimeOffset now)
    {
        if (deviceId == Guid.Empty || actorId == Guid.Empty) throw new ArgumentException("Device and actor are required.");
        Id = Guid.CreateVersion7(); DeviceId = deviceId; Kind = kind; Target = target;
        Generation = Guid.CreateVersion7(); Name = name; Enabled = enabled; TextMode = textMode;
        UpdatedBy = actorId; UpdatedAt = now.ToUniversalTime();
        Validate();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid Generation { get; private set; }
    public AgentLogSourceKind Kind { get; private set; }
    public string Name { get; private set; } = "";
    public string Target { get; private set; } = "";
    public bool Enabled { get; private set; }
    public AgentTextLogMode? TextMode { get; private set; }
    public DateTimeOffset? RetiredAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public AgentLogSourceSetting ToSetting() => new(Id, Generation, Kind, Name, Target, Enabled, 1, TextMode);

    public bool Update(string name, bool enabled, AgentTextLogMode? textMode, Guid actorId, DateTimeOffset now)
    {
        if (RetiredAt is not null) throw new InvalidOperationException("Retired log sources cannot be reused.");
        if (actorId == Guid.Empty) throw new ArgumentException("Actor is required.", nameof(actorId));
        var proposed = new AgentLogSourceSetting(Id, Generation, Kind, name, Target, enabled, 1, textMode);
        if (!proposed.IsValid()) throw new ArgumentException("Invalid log source settings.");
        if (Name == name && Enabled == enabled && TextMode == textMode) return false;
        Name = name; Enabled = enabled; TextMode = textMode; Generation = Guid.CreateVersion7();
        UpdatedBy = actorId; UpdatedAt = now.ToUniversalTime();
        return true;
    }

    public void Retire(Guid actorId, DateTimeOffset now)
    {
        if (RetiredAt is not null) throw new InvalidOperationException("The source is already retired.");
        if (actorId == Guid.Empty) throw new ArgumentException("Actor is required.", nameof(actorId));
        UpdatedAt = now.ToUniversalTime(); UpdatedBy = actorId; RetiredAt = UpdatedAt; Enabled = false;
        Generation = Guid.CreateVersion7();
    }

    private void Validate()
    {
        if (!ToSetting().IsValid()) throw new ArgumentException("Invalid log source settings.");
    }
}
