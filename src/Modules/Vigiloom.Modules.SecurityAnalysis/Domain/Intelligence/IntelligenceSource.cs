using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>One separately managed provider and data subset.</summary>
public sealed class IntelligenceSource
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public required string Key { get; set; }
    public bool Enabled { get; set; }
    public bool IocDetectionEnabled { get; set; }
    public int UpdateIntervalSeconds { get; set; } = 86_400;
    public int StaleAfterSeconds { get; set; } = 259_200;
    public int MaximumBytes { get; set; } = 16 * 1024 * 1024;
    public int MaximumRecords { get; set; } = 50_000;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastSuccessfulCheckAt { get; set; }
    public Guid? LastObservedRevisionId { get; set; }
    public string? LastError { get; set; }
    public string? ProtectedCredential { get; set; }
    public byte[] Version { get; private set; } = [];
}
