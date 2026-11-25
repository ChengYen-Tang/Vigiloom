using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.Services;

internal sealed class RemoteCandidateAdministration(HttpClient client, IDbContextFactory<ServiceObservabilityDbContext> sources) : ICandidateAdministration
{
    public async Task<IReadOnlyList<CandidateSummary>> ListAsync(int page, CancellationToken token)
    {
        var candidates = await client.GetFromJsonAsync<ApplicationCandidateInfo[]>($"internal/v1/application-candidates?page={page}", token)
            ?? throw new InvalidOperationException("Resource service returned an empty candidate list.");
        return candidates.Select(c => new CandidateSummary(c.Id, c.SourceId, c.ServiceKey, c.Environment, c.DisplayName,
            c.FirstSeen, c.LastSeen, c.SourceName, c.ServiceNamespace)).ToArray();
    }

    public async Task<ApprovalResult> ApproveAsync(Guid candidateId, Guid actorId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync($"internal/v1/application-candidates/{candidateId:D}/approve", new ApplicationApprovalRequest(actorId), token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApplicationApprovalResult>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned an empty approval result.");
        var status = result.Status switch
        {
            ApplicationApprovalStatus.Approved => ApprovalStatus.Approved,
            ApplicationApprovalStatus.AlreadyApproved => ApprovalStatus.AlreadyApproved,
            ApplicationApprovalStatus.NotFound => ApprovalStatus.NotFound,
            ApplicationApprovalStatus.Expired => ApprovalStatus.Expired,
            _ => ApprovalStatus.SourceUnavailable
        };
        return new(status, result.ServiceId);
    }

    public async Task<CandidateObservation> ObserveAsync(AuthenticatedSource source, string serviceKey, string environment, string displayName, CancellationToken token, string? serviceNamespace)
    {
        await using var db = await sources.CreateDbContextAsync(token);
        if (source.Version is not { Length: 8 } || !await db.Sources.AsNoTracking().AnyAsync(s => s.Id == source.SourceId && s.RevokedAt == null && s.Version == source.Version, token))
            return CandidateObservation.SourceUnavailable;
        using var response = await client.PostAsJsonAsync("internal/v1/application-candidates/observe",
            new ApplicationObservation(source.SourceId, serviceKey, environment, displayName, serviceNamespace ?? ""), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApplicationObservationStatus>(cancellationToken: token) == ApplicationObservationStatus.AlreadyApproved
            ? CandidateObservation.AlreadyApproved : CandidateObservation.Recorded;
    }
}
