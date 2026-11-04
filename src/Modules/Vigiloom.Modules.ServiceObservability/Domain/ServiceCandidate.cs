namespace Vigiloom.Modules.ServiceObservability.Domain;

/// <summary>Minimum identity metadata for an unapproved service; no telemetry payload is retained.</summary>
public sealed class ServiceCandidate
{
    private ServiceCandidate() { }

    public ServiceCandidate(Guid sourceId, string serviceKey, string environment, string displayName, DateTimeOffset now, string? serviceNamespace = null)
    {
        if (sourceId == Guid.Empty) throw new ArgumentException("A source is required.", nameof(sourceId));
        ServiceKey = ValidText(serviceKey, 128, nameof(serviceKey));
        ServiceNamespace = ValidNamespace(serviceNamespace);
        Environment = ValidText(environment, 64, nameof(environment));
        DisplayName = ValidText(displayName, 128, nameof(displayName));
        Id = Guid.CreateVersion7();
        SourceId = sourceId;
        FirstSeen = LastSeen = CreatedAt = UpdatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid SourceId { get; private set; }
    public string ServiceKey { get; private set; } = "";
    public string ServiceNamespace { get; private set; } = "";
    public byte[] ServiceNamespaceBytes { get; private set; } = [];
    public string Environment { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public DateTimeOffset FirstSeen { get; private set; }
    public DateTimeOffset LastSeen { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Observe(string displayName, DateTimeOffset now)
    {
        DisplayName = ValidText(displayName, 128, nameof(displayName));
        now = now.ToUniversalTime();
        if (LastSeen > now || LastSeen < now.AddMinutes(-3)) FirstSeen = now;
        LastSeen = now;
        UpdatedAt = now;
    }

    internal static string ValidText(string value, int limit, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit) throw new ArgumentException("A bounded non-empty value is required.", name);
        return value.Trim();
    }

    internal static string ValidNamespace(string? value)
    {
        if (value is null) return "";
        if (value.Length > 128) throw new ArgumentException("Namespace exceeds 128 characters.", nameof(value));
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !char.IsLowSurrogate(value[++i])))
                throw new ArgumentException("Namespace must be valid Unicode.", nameof(value));
        return value;
    }
}
