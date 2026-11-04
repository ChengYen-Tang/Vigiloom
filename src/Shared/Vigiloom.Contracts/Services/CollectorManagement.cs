namespace Vigiloom.Contracts.Services;

public sealed record CollectorSummary(Guid Id, string Name, Guid MonitoringServiceId, Guid MonitoringSourceId,
    DateTimeOffset? MonitoringSourceRevokedAt, DateTimeOffset? MonitoringServiceStoppedAt, string Version);
public sealed record CollectorRouteSummary(Guid SourceId, string SourceName, DateTimeOffset? SourceRevokedAt, string Exporter);
