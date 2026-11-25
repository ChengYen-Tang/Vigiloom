using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;

namespace Vigiloom.Web.Authentication;

internal interface IApiCredentialValidation
{
    Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token);
}

// The local implementation remains while the other account operations are being moved.
internal sealed class LocalApiCredentialValidation(ApiCredentialVerifier verifier) : IApiCredentialValidation
{
    public async Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token)
    {
        var credential = await verifier.VerifyAsync(value, token);
        return credential is null ? null : new(credential.Id, credential.Name, credential.CanWrite);
    }
}

internal sealed class RemoteApiCredentialValidation(HttpClient client) : IApiCredentialValidation
{
    public async Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/api-credentials/validate",
            new VerifyApiCredentialRequest("vlg_" + value), token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VerifiedApiCredentialResponse>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty credential response.");
    }
}
