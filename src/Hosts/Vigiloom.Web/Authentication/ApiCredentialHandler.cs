using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

internal sealed class ApiCredentialHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IdentityAccessDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "VigiloomApiCredential";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer vlg_";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        if (!Request.IsHttps) return AuthenticateResult.Fail("A secure connection is required.");
        var value = header[prefix.Length..];
        if (value.Length != 80 || value[36] != '.' || !Guid.TryParseExact(value[..36], "D", out var id))
            return AuthenticateResult.Fail("Invalid credential.");
        byte[] secret;
        try { secret = WebEncoders.Base64UrlDecode(value[37..]); }
        catch (FormatException) { return AuthenticateResult.Fail("Invalid credential."); }
        if (secret.Length != 32) return AuthenticateResult.Fail("Invalid credential.");
        var digest = SHA256.HashData(secret);
        CryptographicOperations.ZeroMemory(secret);
        var credential = await db.ApiCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Context.RequestAborted);
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt <= DateTimeOffset.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(credential.Verifier, digest)) return AuthenticateResult.Fail("Invalid credential.");
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
