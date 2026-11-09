namespace Vigiloom.Contracts.SpeedTests;

public static class SpeedRetentionLimits
{
    public const int DefaultDays = 365;
    public const int MinDays = 1;
    public const int MaxDays = 3650;
    public static bool IsValid(int days) => days is >= MinDays and <= MaxDays;
}

public sealed record SpeedRetentionSnapshot(int Days, string Version, long DeletedTotal, int LastBatchCount, DateTimeOffset? LastBatchAt);
public sealed record SpeedRetentionPreviewRequest(int Days, string Version);
public sealed record SpeedRetentionPreview(int Days, DateTimeOffset PreviewedAt, DateTimeOffset Cutoff,
    int AffectedResults, string Version, string ConfirmationToken);
public sealed record SpeedRetentionInput(int Days, string Version, string? ConfirmationToken);
