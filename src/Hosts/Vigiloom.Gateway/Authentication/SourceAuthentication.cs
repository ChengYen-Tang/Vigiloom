using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Vigiloom.Gateway.Authentication;

internal sealed class SourceAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IHttpClientFactory clients) : RemoteBearerAuthentication(options, logger, encoder, clients)
{
    public const string SchemeName = "GatewaySource";
    protected override string ClientName => SchemeName;
    protected override string IdentityPath => "internal/telemetry/source/identity";
    protected override bool VerifyRejectedRequests => true;
    protected override void ConfigureRequest(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-Vigiloom-Telemetry-Ingress", Request.Path.StartsWithSegments("/api/v1/otlp") ? "otlp" : "discovery");
        request.Headers.TryAddWithoutValidation("X-Vigiloom-Telemetry-Secure", Request.IsHttps ? "true" : "false");
    }

    protected override async Task<Claim[]?> ReadIdentityAsync(HttpResponseMessage response, CancellationToken token)
    {
        var source = await response.Content.ReadFromJsonAsync<SourceIdentity>(cancellationToken: token);
        return source is null || source.SourceId == Guid.Empty ? null : [new(ClaimTypes.NameIdentifier, source.SourceId.ToString("D"))];
    }

    private sealed record SourceIdentity(Guid SourceId);
}
