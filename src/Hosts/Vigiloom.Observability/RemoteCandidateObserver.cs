using System.Net.Http.Json;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Observability;

/// <summary>Sends observed application identities to the resource-owned candidate registry.</summary>
internal sealed class RemoteCandidateObserver(HttpClient client, IDbContextFactory<ServiceObservabilityDbContext> contexts) : ICandidateObserver
{
    public async Task<CandidateObservation> ObserveAsync(AuthenticatedSource source, string serviceKey, string environment,
        string displayName, CancellationToken token, string? serviceNamespace = null)
    {
        if (source.Version is not { Length: 8 }) return CandidateObservation.SourceUnavailable;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var current = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {source.SourceId}")
            .SingleOrDefaultAsync(token);
        if (current is null || current.RevokedAt is not null || !current.Version.AsSpan().SequenceEqual(source.Version))
            return CandidateObservation.SourceUnavailable;
        var result = await ObserveAsync(source.SourceId, serviceKey, environment, displayName, token, serviceNamespace);
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<CandidateObservation> ObserveAsync(Guid sourceId, string serviceKey, string environment,
        string displayName, CancellationToken token, string? serviceNamespace = null)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/application-candidates/observe",
            new ApplicationObservation(sourceId, serviceKey, environment, displayName, serviceNamespace ?? ""), token);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return CandidateObservation.Busy;
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<ApplicationObservationStatus>(cancellationToken: token);
        return status switch
        {
            ApplicationObservationStatus.Recorded => CandidateObservation.Recorded,
            ApplicationObservationStatus.AlreadyApproved => CandidateObservation.AlreadyApproved,
            ApplicationObservationStatus.CapacityReached => CandidateObservation.CapacityReached,
            _ => throw new InvalidOperationException("Unknown application discovery response.")
        };
    }
}
