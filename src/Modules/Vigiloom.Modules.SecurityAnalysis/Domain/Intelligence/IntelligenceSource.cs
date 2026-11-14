using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>One separately managed provider and data subset.</summary>
public sealed class IntelligenceSource
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public required string Key { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastSuccessfulCheckAt { get; set; }
    public string? LastError { get; set; }
    public byte[] Version { get; private set; } = [];
}
