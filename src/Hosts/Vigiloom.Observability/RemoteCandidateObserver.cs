using System.Net.Http.Json;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Observability;

/// <summary>Sends observed application identities to the resource-owned candidate registry.</summary>
internal sealed class RemoteCandidateObserver(HttpClient client) : ICandidateObserver
{
    public Task<CandidateObservation> ObserveAsync(AuthenticatedSource source, string serviceKey, string environment,
        string displayName, CancellationToken token, string? serviceNamespace = null) =>
        source.Version is { Length: 8 }
            ? ObserveAsync(source.SourceId, serviceKey, environment, displayName, token, serviceNamespace)
            : Task.FromResult(CandidateObservation.SourceUnavailable);

    public async Task<CandidateObservation> ObserveAsync(Guid sourceId, string serviceKey, string environment,
        string displayName, CancellationToken token, string? serviceNamespace = null)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/application-candidates/observe",
            new ApplicationObservation(sourceId, serviceKey, environment, displayName, serviceNamespace ?? ""), token);
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<ApplicationObservationStatus>(cancellationToken: token);
        return status switch
        {
            ApplicationObservationStatus.Recorded => CandidateObservation.Recorded,
            ApplicationObservationStatus.AlreadyApproved => CandidateObservation.AlreadyApproved,
            _ => throw new InvalidOperationException("Unknown application discovery response.")
        };
    }
}
