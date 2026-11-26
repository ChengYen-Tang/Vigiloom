namespace Vigiloom.Contracts.Services;

/// <summary>Allowed retention periods for application telemetry.</summary>
public static class TelemetryRetentionLimits
{
    public static bool IsValid(int logDays, int traceDays) => logDays is >= 1 and <= 3650 && traceDays is >= 1 and <= 3650;
    public static bool IsValidMetrics(int rawDays, int rollupDays) => rawDays is >= 1 and <= 3650 && rollupDays >= rawDays && rollupDays <= 3650;
    public static bool IsValidHealth(int days) => days is >= 1 and <= 3650;
}

public sealed record TelemetryRetentionSnapshot(int LogDays, int TraceDays, string Version, int MetricRawDays = 30, int MetricRollupDays = 365, int HealthDays = 365);
public sealed record TelemetryRetentionPreviewRequest(int LogDays, int TraceDays, string Version, int? MetricRawDays = null, int? MetricRollupDays = null, int? HealthDays = null);
public sealed record TelemetryRetentionPreview(int LogDays, int TraceDays, DateTimeOffset PreviewedAt, long Logs, long Spans, string Version, string ConfirmationToken,
    int MetricRawDays = 30, int MetricRollupDays = 365, long MetricPoints = 0, long MetricWindows = 0, int HealthDays = 365, long HealthObservations = 0, long HealthIntervals = 0, long HostLogs = 0, long SecurityDetections = 0);
public sealed record TelemetryRetentionInput(int LogDays, int TraceDays, string Version, string? ConfirmationToken, int? MetricRawDays = null, int? MetricRollupDays = null, int? HealthDays = null);

/// <summary>Internal requests retain the initiating administrator for preview and audit.</summary>
public sealed record TelemetryRetentionPreviewCommand(Guid ActorId, TelemetryRetentionPreviewRequest Input);
public sealed record TelemetryRetentionUpdateCommand(Guid ActorId, TelemetryRetentionInput Input);

public sealed record HostLogCleanupBatch(int Removed, bool HasMore);
