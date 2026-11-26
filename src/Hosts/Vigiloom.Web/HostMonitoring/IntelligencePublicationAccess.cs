using System.Net.Http.Json;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class IntelligencePublicationAccess(IntelligencePublication publication, IntelligenceRevocation revocation, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => configuration.GetValue("Services:Security:IntelligenceEnabled", false) &&
        !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);

    public Task<IntelligencePublicationResult> ActivateAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, bool confirmLargeReduction, Guid actorId, CancellationToken token) =>
        ActivateAsync(sourceId, revisionId, expectedActiveRevisionId, expectedVersion, confirmLargeReduction, false, actorId, token);

    public async Task<IntelligencePublicationResult> ActivateAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, bool confirmLargeReduction, bool confirmOfflineSource, Guid actorId, CancellationToken token)
    {
        if (!Remote) return await publication.ActivateAsync(sourceId, revisionId, expectedActiveRevisionId, expectedVersion, confirmLargeReduction, confirmOfflineSource, actorId, token);
        using var response = await clients.CreateClient("security").PostAsJsonAsync($"internal/v1/intelligence/sources/{sourceId:D}/activation",
            new { Input = new IntelligenceActivationRequest(revisionId, expectedActiveRevisionId, expectedVersion, confirmLargeReduction, confirmOfflineSource), ActorId = actorId }, token);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) throw new ArgumentException("Invalid activation request.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IntelligencePublicationResult>(cancellationToken: token);
    }

    public async Task<IntelligenceRevocationResult> RevokeAsync(Guid sourceId, Guid revisionId, Guid actorId, CancellationToken token)
    {
        if (!Remote) return await revocation.RevokeAsync(sourceId, revisionId, actorId, token);
        using var response = await clients.CreateClient("security").PostAsJsonAsync($"internal/v1/intelligence/sources/{sourceId:D}/revisions/{revisionId:D}/revoke", new { ActorId = actorId }, token);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) throw new ArgumentException("Invalid revocation request.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IntelligenceRevocationResult>(cancellationToken: token);
    }
}
