using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Web.Telemetry;

namespace Vigiloom.Web.Authentication;

internal sealed class ServiceSourceCredentialHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, SourceCredentials sources, ReceptionRejectionDiagnostics diagnostics) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "VigiloomServiceSource";
    private const string VersionClaim = "vigiloom:source-version";

    internal static AuthenticatedSource? FromPrincipal(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return null;
        var encoded = principal.FindFirstValue(VersionClaim);
        if (encoded is null) return null;
        Span<byte> bytes = stackalloc byte[8];
        return Convert.TryFromBase64String(encoded, bytes, out var written) && written == bytes.Length
            ? new(id, bytes.ToArray()) : null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        var ingress = Request.Path.StartsWithSegments(OtlpHttpEndpoints.Prefix) ? ReceptionIngress.Otlp : ReceptionIngress.Discovery;
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { diagnostics.Record(ReceptionRejectionKind.CredentialRejected, ingress); return AuthenticateResult.NoResult(); }
        if (!Request.IsHttps) { diagnostics.Record(ReceptionRejectionKind.CredentialRejected, ingress); return AuthenticateResult.Fail("A secure connection is required."); }
        var source = await sources.AuthenticateVersionedAsync(header[prefix.Length..], Context.RequestAborted);
        if (source is null || source.Version.Length != 8) { diagnostics.Record(ReceptionRejectionKind.CredentialRejected, ingress); return AuthenticateResult.Fail("Invalid credential."); }
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, source.SourceId.ToString("D")),
            new Claim(VersionClaim, Convert.ToBase64String(source.Version)),
            new Claim("vigiloom:principal-type", "service-source")
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
