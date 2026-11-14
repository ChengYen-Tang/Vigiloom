using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>A durable, bounded import window that cannot be published until reconciliation.</summary>
public sealed class AdvisoryImport
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid SourceId { get; set; }
    public Guid? BaseRevisionId { get; set; }
    public byte[]? BaseActivationVersion { get; set; }
    public required byte[] SourceVersion { get; set; }
    public DateOnly WindowStart { get; set; }
    public DateOnly WindowEnd { get; set; }
    public int Phase { get; set; } = 1;
    public string? Cursor { get; set; }
    public int PageCount { get; set; }
    public int RecordCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public int RateLimitAttempts { get; set; }
    public int RestartCount { get; set; }
    public Guid? SuccessorId { get; set; }
    public Guid? ResultRevisionId { get; set; }
    public int ReconciliationPhase { get; set; } = 1;
    public int ReconciliationRecordCount { get; set; }
    public byte[] Version { get; private set; } = [];
}
