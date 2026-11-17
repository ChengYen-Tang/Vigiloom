namespace Vigiloom.Contracts.Operations;

public static class AuditRetentionLimits
{
    public static bool IsValid(int days) => days is >= 1 and <= 3650;
}

public sealed record AuditRetentionSnapshot(int Days, string Version);
public sealed record AuditRetentionPreviewRequest(int Days, string Version);
public sealed record AuditRetentionPreview(int Days, DateTimeOffset PreviewedAt, long AffectedRecords, string Version, string ConfirmationToken, long AlertEpisodes = 0, long Notifications = 0);
public sealed record AuditRetentionInput(int Days, string Version, string? ConfirmationToken);
