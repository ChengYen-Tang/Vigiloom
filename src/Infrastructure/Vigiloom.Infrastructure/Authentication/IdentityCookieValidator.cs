using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;

namespace Vigiloom.Infrastructure.Authentication;

public sealed class IdentityCookieValidator(HttpClient client, IOptions<IdentityOptions> options)
{
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.FindFirstValue(options.Value.ClaimsIdentity.UserIdClaimType);
        var stamp = context.Principal?.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        ValidatedCookieResponse? validated = null;
        if (Guid.TryParse(id, out var accountId) && !string.IsNullOrEmpty(stamp))
        {
            using var response = await client.PostAsJsonAsync("internal/identity/cookies/validate",
                new ValidateSessionRequest(accountId, stamp), context.HttpContext.RequestAborted);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                response.EnsureSuccessStatusCode();
                validated = await response.Content.ReadFromJsonAsync<ValidatedCookieResponse>(cancellationToken: context.HttpContext.RequestAborted)
                    ?? throw new InvalidOperationException("Identity service returned an empty cookie response.");
            }
        }
        if (validated is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            await context.HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            await context.HttpContext.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
            await context.HttpContext.SignOutAsync(IdentityConstants.TwoFactorRememberMeScheme);
            return;
        }
        var claims = validated.Claims.Select(claim => new Claim(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer));
        var identity = new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme,
            options.Value.ClaimsIdentity.UserNameClaimType, options.Value.ClaimsIdentity.RoleClaimType);
        context.ReplacePrincipal(new ClaimsPrincipal(identity));
        context.ShouldRenew = true;
    }
}

public static class IdentityCookieClientRegistration
{
    public static IServiceCollection AddVigiloomIdentityCookieClient(this IServiceCollection services)
    {
        services.AddServiceDiscovery();
        services.AddHttpClient<IdentityCookieValidator>((provider, client) =>
            client.BaseAddress = new Uri(provider.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .AddServiceDiscovery();
        return services;
    }
}
