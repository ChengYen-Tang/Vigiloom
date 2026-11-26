using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Web.Authentication;
using ServiceDetailsDto = Vigiloom.Contracts.Services.ServiceDetails;

namespace Vigiloom.Web.ServiceDetails;

internal sealed class ServerServiceDetailsClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities,
    ServiceQueries services, DeploymentLinks deployments) : IServiceDetailsClient
{
    public async Task<ServiceDetailsDto?> GetServiceAsync(Guid id, CancellationToken token)
    { await AuthorizeAsync(token); return await services.DetailAsync(id, token); }
    public async Task<ServiceInstancePage?> GetInstancesAsync(Guid id, int page, CancellationToken token)
    { await AuthorizeAsync(token); return await services.InstancesAsync(id, page, token); }
    public async Task<DeploymentLinkPage?> GetDeploymentsAsync(Guid id, int page, CancellationToken token)
    { await AuthorizeAsync(token); return await deployments.ListAsync(id, page, token); }
    public async Task<ServiceHealthStatus?> GetHealthAsync(Guid id, CancellationToken token)
    { await AuthorizeAsync(token); return await services.StatusAsync(id, token); }
    public async Task<HealthObservationPage> GetHealthEvidenceAsync(Guid id, int page, CancellationToken token)
    { await AuthorizeAsync(token); return await services.ObservationsAsync(id, page, token); }
    public async Task<ServiceHealthHistory?> GetHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    { await AuthorizeAsync(token); return await services.HistoryAsync(id, from, to, token); }
    public async Task<ServiceHttpMetrics> GetHttpMetricsAsync(Guid id, CancellationToken token)
    { await AuthorizeAsync(token); return await services.MetricsAsync(id, token); }
    public async Task<ServiceEvidencePage<ServiceLogEvidence>> GetLogsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, int page, string? traceId, CancellationToken token)
    { await AuthorizeAsync(token); return await services.LogsAsync(id, from, to, page, traceId, token); }
    public async Task<ServiceEvidencePage<ServiceTraceEvidence>> GetTracesAsync(Guid id, DateTimeOffset from, DateTimeOffset to, int page, CancellationToken token)
    { await AuthorizeAsync(token); return await services.TracesAsync(id, from, to, page, token); }

    private async Task AuthorizeAsync(CancellationToken token)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (!SessionLifetime.IsCurrent(state.User) || await identities.GetAsync(state.User, token) is null)
            throw new UnauthorizedAccessException();
    }
}
