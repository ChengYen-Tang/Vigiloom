using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Application;

namespace Vigiloom.Web.Authentication;

internal sealed class ApiCredentialHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, ApiCredentialVerifier verifier) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "VigiloomApiCredential";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer vlg_";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        if (!Request.IsHttps) return AuthenticateResult.Fail("A secure connection is required.");
        var credential = await verifier.VerifyAsync(header[prefix.Length..], Context.RequestAborted);
        if (credential is null) return AuthenticateResult.Fail("Invalid credential.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, credential.Id.ToString("D")),
            new(ClaimTypes.Name, credential.Name),
            new("vigiloom:principal-type", "api-client"),
            new("scope", "monitoring.read")
        };
        if (credential.CanWrite) claims.Add(new Claim("scope", "monitoring.write"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
