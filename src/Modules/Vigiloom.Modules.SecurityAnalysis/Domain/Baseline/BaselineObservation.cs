using Vigiloom.Contracts.Agent.Baseline;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Baseline;

/// <summary>A read-only baseline observation attributed to the authenticated Agent.</summary>
public sealed class BaselineObservation
{
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public Guid BatchId { get; set; }
    public Guid SourceId { get; set; }
    public Guid RevisionId { get; set; }
    public required string ActivationVersion { get; set; }
    public required string CheckId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required string OperatingSystem { get; set; }
    public BaselineOutcome Outcome { get; set; }
    public string? Evidence { get; set; }
}
