using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Observability;

/// <summary>Registers known internal services through the resource service's trusted endpoint.</summary>
internal sealed class SelfCandidateObserver(HttpClient resources) : ICandidateObserver
{
    public Task<CandidateObservation> ObserveAsync(AuthenticatedSource source, string serviceKey, string environment,
        string displayName, CancellationToken token, string? serviceNamespace = null) =>
        ObserveAsync(source.SourceId, serviceKey, environment, displayName, token, serviceNamespace);

    public async Task<CandidateObservation> ObserveAsync(Guid sourceId, string serviceKey, string environment,
        string displayName, CancellationToken token, string? serviceNamespace = null)
    {
        using var response = await resources.PostAsJsonAsync("internal/v1/self-services/register",
            new ApplicationObservation(sourceId, serviceKey, environment, displayName, serviceNamespace ?? ""), token);
        if (response.StatusCode == HttpStatusCode.BadRequest) return CandidateObservation.SourceUnavailable;
        response.EnsureSuccessStatusCode();
        return CandidateObservation.AlreadyApproved;
    }
}
