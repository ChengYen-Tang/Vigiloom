namespace Vigiloom.Modules.ServiceObservability.Domain.Health;

/// <summary>An administrator-configured health endpoint owned by one approved service.</summary>
public sealed class HealthTarget
{
    private HealthTarget() { }

    public HealthTarget(Guid serviceId, Guid? instanceId, string endpoint, int intervalSeconds,
        int timeoutSeconds, int failureThreshold, int recoveryThreshold, DateTimeOffset now)
    {
        if (serviceId.Version != 7 || (instanceId is { } id && id.Version != 7))
            throw new ArgumentException("A valid service and optional instance are required.");
        Id = Guid.CreateVersion7();
        ServiceId = serviceId;
        InstanceId = instanceId;
        Apply(endpoint, intervalSeconds, timeoutSeconds, failureThreshold, recoveryThreshold, now);
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid ServiceId { get; private set; }
    public Guid? InstanceId { get; private set; }
    public string Endpoint { get; private set; } = "";
    public int IntervalSeconds { get; private set; }
    public int TimeoutSeconds { get; private set; }
    public int FailureThreshold { get; private set; }
    public int RecoveryThreshold { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public void Apply(string endpoint, int intervalSeconds, int timeoutSeconds,
        int failureThreshold, int recoveryThreshold, DateTimeOffset now)
    {
        if (endpoint is null || endpoint.Length > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsoluteUri.Length > 2048)
            throw new ArgumentException("Use an HTTP or HTTPS endpoint without embedded credentials or a fragment.", nameof(endpoint));
        if (intervalSeconds is < 5 or > 3600 || timeoutSeconds is < 1 or > 60 || timeoutSeconds > intervalSeconds ||
            failureThreshold is < 1 or > 10 || recoveryThreshold is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "Invalid health check interval, timeout or threshold.");
        Endpoint = uri.AbsoluteUri;
        IntervalSeconds = intervalSeconds;
        TimeoutSeconds = timeoutSeconds;
        FailureThreshold = failureThreshold;
        RecoveryThreshold = recoveryThreshold;
        UpdatedAt = now.ToUniversalTime();
        if (CreatedAt == default) CreatedAt = UpdatedAt;
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        Enabled = enabled;
        UpdatedAt = now.ToUniversalTime();
    }
}
