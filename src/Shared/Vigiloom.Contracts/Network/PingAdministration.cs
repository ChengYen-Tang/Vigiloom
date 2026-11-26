namespace Vigiloom.Contracts.Network;

public interface IPingSettings
{
    Task<PingSettingsSnapshot> GetAsync(CancellationToken token);
    Task<PingSettingsSnapshot?> UpdateAsync(PingSettingsInput input, Guid actor, CancellationToken token);
}

public interface IPingRetentionSettings
{
    Task<PingRetentionSnapshot> GetAsync(CancellationToken token);
    Task<PingRetentionPreview?> PreviewAsync(PingRetentionPreviewRequest input, Guid actor, CancellationToken token);
    Task<PingRetentionSnapshot?> UpdateAsync(PingRetentionInput input, Guid actor, CancellationToken token);
}

public sealed record PingSettingsCommand(PingSettingsInput Input, Guid Actor);
public sealed record PingRetentionPreviewCommand(PingRetentionPreviewRequest Input, Guid Actor);
public sealed record PingRetentionCommand(PingRetentionInput Input, Guid Actor);
