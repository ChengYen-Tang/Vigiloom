namespace Vigiloom.Contracts.Services;

/// <summary>Authorized service detail and health queries for either Blazor render mode.</summary>
public interface IServiceDetailsClient
{
    Task<ServiceDetails?> GetServiceAsync(Guid id, CancellationToken token);
    Task<ServiceInstancePage?> GetInstancesAsync(Guid id, int page, CancellationToken token);
    Task<ServiceHealthStatus?> GetHealthAsync(Guid id, CancellationToken token);
    Task<HealthObservationPage> GetHealthEvidenceAsync(Guid id, int page, CancellationToken token);
    Task<ServiceHealthHistory?> GetHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token);
    Task<ServiceHttpMetrics> GetHttpMetricsAsync(Guid id, CancellationToken token);
    Task<ServiceEvidencePage<ServiceLogEvidence>> GetLogsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, int page, string? traceId, CancellationToken token);
    Task<ServiceEvidencePage<ServiceTraceEvidence>> GetTracesAsync(Guid id, DateTimeOffset from, DateTimeOffset to, int page, CancellationToken token);
}
