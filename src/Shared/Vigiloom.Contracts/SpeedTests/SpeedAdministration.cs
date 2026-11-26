namespace Vigiloom.Contracts.SpeedTests;

public interface ISpeedSettings
{
    Task<SpeedSettingsSnapshot> GetAsync(CancellationToken token);
    Task<SpeedSettingsSnapshot?> UpdateAsync(SpeedSettingsInput input, Guid actor, CancellationToken token);
}
public interface ISpeedRetentionSettings
{
    Task<SpeedRetentionSnapshot> GetAsync(CancellationToken token);
    Task<SpeedRetentionPreview?> PreviewAsync(int days, string version, Guid actor, CancellationToken token);
    Task<SpeedRetentionSnapshot?> UpdateAsync(SpeedRetentionInput input, Guid actor, CancellationToken token);
}
public interface ISpeedCancellation
{
    Task<bool> CancelAsync(Guid id, CancellationToken token);
}
public sealed record SpeedSettingsCommand(SpeedSettingsInput Input, Guid Actor);
public sealed record SpeedRetentionPreviewCommand(int Days, string Version, Guid Actor);
public sealed record SpeedRetentionCommand(SpeedRetentionInput Input, Guid Actor);
