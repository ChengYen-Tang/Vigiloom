using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Web.Authentication;

internal sealed class ServiceSourceCredentialHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, SourceCredentials sources) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "VigiloomServiceSource";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        if (!Request.IsHttps) return AuthenticateResult.Fail("A secure connection is required.");
        var sourceId = await sources.AuthenticateAsync(header[prefix.Length..], Context.RequestAborted);
        if (sourceId is null) return AuthenticateResult.Fail("Invalid credential.");
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, sourceId.Value.ToString("D")),
            new Claim("vigiloom:principal-type", "service-source")
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
