using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Vigiloom.Web.Authentication;

/// <summary>Public framework assets do not need a user or a per-file security-stamp lookup.</summary>
internal sealed class PublicAssetAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SelectorScheme = "VigiloomSessionSelector";
    public const string AssetScheme = "VigiloomPublicAsset";
    public static bool IsPublicAsset(PathString path) =>
        path.StartsWithSegments("/_framework") || path.StartsWithSegments("/_content");
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}
