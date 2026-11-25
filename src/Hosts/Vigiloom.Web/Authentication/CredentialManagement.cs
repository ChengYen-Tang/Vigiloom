using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;

namespace Vigiloom.Web.Authentication;

internal sealed class CredentialManagement(IServiceProvider services, IConfiguration configuration, IOptions<IdentityOptions> options)
{
    public Task<ApiCredentialPage> ListAsync(ClaimsPrincipal principal, int page, CancellationToken token) =>
        Remote ? Client().ListAsync(Actor(principal), page, token) : services.GetRequiredService<ApiCredentials>().ListAsync(principal, page, token);

    public Task<ApiCredentialIssueResult> IssueAsync(ClaimsPrincipal principal, string name, bool canWrite, int days, string password, CancellationToken token) =>
        Remote ? Client().IssueAsync(Actor(principal), name, canWrite, days, password, token) : services.GetRequiredService<ApiCredentials>().IssueAsync(principal, name, canWrite, days, password, token);

    public Task<ApiCredentialStatus> RevokeAsync(ClaimsPrincipal principal, Guid id, string password, CancellationToken token) =>
        Remote ? Client().RevokeAsync(Actor(principal), id, password, token) : services.GetRequiredService<ApiCredentials>().RevokeAsync(principal, id, password, token);

    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Identity:Url"]);
    private RemoteCredentialManagement Client() => services.GetRequiredService<RemoteCredentialManagement>();
    private IdentityActor Actor(ClaimsPrincipal principal)
    {
        if (principal.HasClaim(claim => claim.Type == SessionLifetime.ExpirationClaim) && !SessionLifetime.IsCurrent(principal))
            throw new UnauthorizedAccessException();
        var id = principal.FindFirstValue(options.Value.ClaimsIdentity.UserIdClaimType);
        var stamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        if (!Guid.TryParse(id, out var accountId) || string.IsNullOrEmpty(stamp)) throw new UnauthorizedAccessException();
        return new IdentityActor(accountId, stamp);
    }
}

internal sealed class RemoteCredentialManagement(HttpClient client)
{
    public Task<ApiCredentialPage> ListAsync(IdentityActor actor, int page, CancellationToken token) =>
        SendAsync<CredentialListRequest, ApiCredentialPage>("list", new(actor, page), token);
    public Task<ApiCredentialIssueResult> IssueAsync(IdentityActor actor, string name, bool canWrite, int days, string password, CancellationToken token) =>
        SendAsync<CredentialIssueRequest, ApiCredentialIssueResult>("issue", new(actor, name, canWrite, days, password), token);
    public Task<ApiCredentialStatus> RevokeAsync(IdentityActor actor, Guid id, string password, CancellationToken token) =>
        SendAsync<CredentialRevokeRequest, ApiCredentialStatus>("revoke", new(actor, id, password), token);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(string action, TRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/credentials/" + action, request, token);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty credential management response.");
    }
}
