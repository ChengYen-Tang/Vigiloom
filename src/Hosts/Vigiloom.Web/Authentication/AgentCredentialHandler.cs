using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.Authentication;

internal sealed class AgentCredentialHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, AgentIdentity agents) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "VigiloomAgentCredential";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        if (!Request.IsHttps) return AuthenticateResult.Fail("A secure connection is required.");
        var agent = await agents.AuthenticateAsync(header[prefix.Length..], Context.RequestAborted);
        if (agent is null) return AuthenticateResult.Fail("Invalid credential.");
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, agent.AgentId.ToString("D")),
            new Claim("vigiloom:principal-type", "agent"),
            new Claim("vigiloom:device-id", agent.DeviceId.ToString("D")),
            new Claim("vigiloom:device-generation", agent.DeviceGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
