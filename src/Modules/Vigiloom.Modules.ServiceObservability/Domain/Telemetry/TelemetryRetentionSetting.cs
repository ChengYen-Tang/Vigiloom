using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

public sealed class TelemetryRetentionSetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public int Slot { get; set; } = 1;
    public int LogDays { get; set; } = 14;
    public int TraceDays { get; set; } = 7;
    public int MetricRawDays { get; set; } = 30;
    public int MetricRollupDays { get; set; } = 365;
    public int HealthDays { get; set; } = 365;
    public byte[] Version { get; private set; } = [];
}
