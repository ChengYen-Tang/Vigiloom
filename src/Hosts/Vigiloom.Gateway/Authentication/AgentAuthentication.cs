using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Vigiloom.Gateway.Authentication;

internal sealed class AgentAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IHttpClientFactory clients) : RemoteBearerAuthentication(options, logger, encoder, clients)
{
    public const string SchemeName = "GatewayAgent";
    protected override string ClientName => SchemeName;
    protected override string IdentityPath => "api/v1/agent/identity";

    protected override async Task<Claim[]?> ReadIdentityAsync(HttpResponseMessage response, CancellationToken token)
    {
        var agent = await response.Content.ReadFromJsonAsync<AgentIdentity>(cancellationToken: token);
        return agent is null || agent.AgentId == Guid.Empty || agent.DeviceId == Guid.Empty ? null :
        [new(ClaimTypes.NameIdentifier, agent.AgentId.ToString("D")), new("vigiloom:device-id", agent.DeviceId.ToString("D"))];
    }

    private sealed record AgentIdentity(Guid AgentId, Guid DeviceId);
}
