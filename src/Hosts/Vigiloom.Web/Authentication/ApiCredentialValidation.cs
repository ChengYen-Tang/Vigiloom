using Vigiloom.Contracts.Identity;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.IdentityAccess.Application;

namespace Vigiloom.Web.Authentication;

// The local implementation remains while the other account operations are being moved.
internal sealed class LocalApiCredentialValidation(ApiCredentialVerifier verifier) : IApiCredentialValidation
{
    public async Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token)
    {
        var credential = await verifier.VerifyAsync(value, token);
        return credential is null ? null : new(credential.Id, credential.Name, credential.CanWrite);
    }
}
