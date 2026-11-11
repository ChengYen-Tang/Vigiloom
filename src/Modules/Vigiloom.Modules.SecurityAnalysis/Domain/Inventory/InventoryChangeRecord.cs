using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;

/// <summary>A change between two complete snapshots, attributed to an authenticated Agent.</summary>
public sealed class InventoryChangeRecord
{
    private InventoryChangeRecord() { }

    public long RowId { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid DeviceId { get; private set; }
    public InventorySource Source { get; private set; }
    public long Generation { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
    public int Kind { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }

    internal InventoryChangeRecord(InventorySnapshot snapshot, DateTimeOffset recordedAt, int kind, string key, string? beforeJson, string? afterJson)
    {
        AgentId = snapshot.AgentId;
        DeviceId = snapshot.DeviceId;
        Source = snapshot.Source;
        Generation = snapshot.Generation;
        ObservedAt = snapshot.ObservedAt;
        RecordedAt = recordedAt;
        Kind = kind;
        Key = key;
        BeforeJson = beforeJson;
        AfterJson = afterJson;
    }
}
