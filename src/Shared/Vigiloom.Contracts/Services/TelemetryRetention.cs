namespace Vigiloom.Contracts.Services;

/// <summary>Allowed retention periods for application logs and traces.</summary>
public static class TelemetryRetentionLimits
{
    public static bool IsValid(int logDays, int traceDays) => logDays is >= 1 and <= 3650 && traceDays is >= 1 and <= 3650;
}

public sealed record TelemetryRetentionSnapshot(int LogDays, int TraceDays, string Version);
public sealed record TelemetryRetentionPreviewRequest(int LogDays, int TraceDays, string Version);
public sealed record TelemetryRetentionPreview(int LogDays, int TraceDays, DateTimeOffset PreviewedAt, long Logs, long Spans, string Version, string ConfirmationToken);
public sealed record TelemetryRetentionInput(int LogDays, int TraceDays, string Version, string? ConfirmationToken);
