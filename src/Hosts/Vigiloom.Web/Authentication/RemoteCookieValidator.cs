using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Identity;

namespace Vigiloom.Web.Authentication;

internal sealed class RemoteCookieValidator(HttpClient client)
{
    public async Task<bool> RevokeSessionsAsync(string? id, string? stamp, CancellationToken token)
    {
        if (!Guid.TryParse(id, out var accountId) || string.IsNullOrEmpty(stamp)) return true;
        using var response = await client.PostAsJsonAsync("internal/identity/sessions/revoke",
            new ValidateSessionRequest(accountId, stamp), token);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> ValidateTwoFactorAsync(string? id, string? stamp, CancellationToken token)
    {
        if (!Guid.TryParse(id, out var accountId) || string.IsNullOrEmpty(stamp)) return false;
        using var response = await client.PostAsJsonAsync("internal/identity/two-factor/validate",
            new ValidateSessionRequest(accountId, stamp), token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
