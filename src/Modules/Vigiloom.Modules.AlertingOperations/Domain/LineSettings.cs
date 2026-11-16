namespace Vigiloom.Modules.AlertingOperations.Domain;

/// <summary>One LINE channel shared by the central platform.</summary>
public sealed class LineSettings
{
    public int Slot { get; set; } = 1;
    public bool Enabled { get; set; }
    public long LastNotificationRowId { get; set; }
    public string? ProtectedAccessToken { get; set; }
    public string? ProtectedChannelSecret { get; set; }
}
