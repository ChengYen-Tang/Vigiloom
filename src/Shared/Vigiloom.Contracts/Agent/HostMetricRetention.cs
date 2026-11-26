namespace Vigiloom.Contracts.Agent;

/// <summary>Allowed storage periods for host metric samples and summaries.</summary>
public static class HostMetricRetentionLimits
{
    public static bool IsValid(int rawDays, int rollupDays) =>
        rawDays is >= 1 and <= 3650 && rollupDays >= rawDays && rollupDays <= 3650;
}

public sealed record HostMetricRetentionSnapshot(int RawDays, int RollupDays, string Version);
public sealed record HostMetricRetentionPreviewRequest(int RawDays, int RollupDays, string Version);
public sealed record HostMetricRetentionPreview(int RawDays, int RollupDays, DateTimeOffset PreviewedAt, long Samples, long Rollups, string Version, string ConfirmationToken);
public sealed record HostMetricRetentionInput(int RawDays, int RollupDays, string Version, string? ConfirmationToken);
public sealed record HostMetricRetentionPreviewCommand(HostMetricRetentionPreviewRequest Input, Guid Actor);
public sealed record HostMetricRetentionCommand(HostMetricRetentionInput Input, Guid Actor);

public interface IHostMetricRetentionSettings
{
    Task<HostMetricRetentionSnapshot> GetAsync(CancellationToken token);
    Task<HostMetricRetentionPreview?> PreviewAsync(HostMetricRetentionPreviewRequest input, Guid actor, CancellationToken token);
    Task<HostMetricRetentionSnapshot?> UpdateAsync(HostMetricRetentionInput input, Guid actor, CancellationToken token);
}
