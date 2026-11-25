using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Web.Authentication;

internal sealed class RemoteWorkspaceIdentity(HttpClient client, IOptions<IdentityOptions> options)
{
    public async Task<WorkspaceIdentity?> GetAsync(ClaimsPrincipal principal, CancellationToken token)
    {
        var session = await ValidateAsync(principal, token);
        if (session is null || (!session.Roles.Contains(BuiltInRoles.Administrator) && !session.Roles.Contains(BuiltInRoles.ReadOnly))) return null;
        return new WorkspaceIdentity(session.UserName, session.Roles);
    }

    public async Task<ValidatedSessionResponse?> ValidateAsync(ClaimsPrincipal principal, CancellationToken token, bool allowLockedOut = false)
    {
        if (principal.HasClaim(claim => claim.Type == SessionLifetime.ExpirationClaim) && !SessionLifetime.IsCurrent(principal)) return null;
        var id = principal.FindFirstValue(options.Value.ClaimsIdentity.UserIdClaimType);
        var stamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        if (!Guid.TryParse(id, out var accountId) || string.IsNullOrEmpty(stamp)) return null;
        using var response = await client.PostAsJsonAsync("internal/identity/sessions/validate", new ValidateSessionRequest(accountId, stamp, allowLockedOut), token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ValidatedSessionResponse>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty session response.");
    }
}
