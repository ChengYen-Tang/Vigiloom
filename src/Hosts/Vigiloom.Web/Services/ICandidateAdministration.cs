using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Web.Services;

internal interface ICandidateAdministration
{
    Task<IReadOnlyList<CandidateSummary>> ListAsync(int page, CancellationToken token);
    Task<ApprovalResult> ApproveAsync(Guid candidateId, Guid actorId, CancellationToken token);
    Task<CandidateObservation> ObserveAsync(AuthenticatedSource source, string serviceKey, string environment, string displayName, CancellationToken token, string? serviceNamespace);
}

internal sealed class LocalCandidateAdministration(CandidateDiscovery discovery, ServiceApproval approval) : ICandidateAdministration
{
    public Task<IReadOnlyList<CandidateSummary>> ListAsync(int page, CancellationToken token) => discovery.ListAsync(page, token);
    public Task<ApprovalResult> ApproveAsync(Guid candidateId, Guid actorId, CancellationToken token) => approval.ApproveAsync(candidateId, actorId, token);
    public Task<CandidateObservation> ObserveAsync(AuthenticatedSource source, string serviceKey, string environment, string displayName, CancellationToken token, string? serviceNamespace) =>
        discovery.ObserveAsync(source, serviceKey, environment, displayName, token, serviceNamespace);
}
