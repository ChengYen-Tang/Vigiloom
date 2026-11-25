using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Web.Authentication;

internal sealed class TwoFactorSettings(IServiceProvider services, IConfiguration configuration, IOptions<IdentityOptions> options)
{
    private IdentityClaim[]? renewalClaims;
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Identity:Url"]);
    private TwoFactorManagement Local => services.GetRequiredService<TwoFactorManagement>();
    private RemoteTwoFactorSettings Client => services.GetRequiredService<RemoteTwoFactorSettings>();
    private TwoFactorSettingsRequest Request(ClaimsPrincipal principal, string? password = null, string? code = null) =>
        new(IdentityActorFactory.Create(principal, options.Value), password, code);

    public async Task<TwoFactorSettingsState> StateAsync(ClaimsPrincipal principal, CancellationToken token)
    {
        if (Remote) return await Client.SendAsync<TwoFactorSettingsState>("state", Request(principal), token);
        var users = services.GetRequiredService<UserManager<Account>>();
        var user = await users.GetUserAsync(principal);
        var enabled = user is not null && await users.GetTwoFactorEnabledAsync(user);
        return new(enabled, user is not null && !enabled && await users.GetAuthenticatorKeyAsync(user) is not null);
    }
    public Task<TwoFactorResult> BeginAsync(ClaimsPrincipal principal, string password, CancellationToken token) =>
        Remote ? InvokeAsync("begin", Request(principal, password: password), token) : Local.BeginAsync(principal, password, token);
    public Task<TwoFactorResult> EnableAsync(ClaimsPrincipal principal, string code, CancellationToken token) =>
        Remote ? InvokeAsync("enable", Request(principal, code: code), token) : Local.EnableAsync(principal, code, token);
    public Task<TwoFactorResult> DisableAsync(ClaimsPrincipal principal, string password, string code, CancellationToken token) =>
        Remote ? InvokeAsync("disable", Request(principal, password, code), token) : Local.DisableAsync(principal, password, code, token);

    private async Task<TwoFactorResult> InvokeAsync(string action, TwoFactorSettingsRequest request, CancellationToken token)
    {
        var response = await Client.SendAsync<TwoFactorSettingsResponse>(action, request, token);
        renewalClaims = response.Claims;
        return new((TwoFactorStatus)response.Status, response.Key, response.RecoveryCodes);
    }

    public async Task RenewCookieAsync(HttpContext context, bool persistent)
    {
        if (!Remote)
        {
            var users = services.GetRequiredService<UserManager<Account>>();
            var user = await users.FindByIdAsync(users.GetUserId(context.User) ?? "")
                ?? throw new InvalidOperationException("The current account no longer exists.");
            await services.GetRequiredService<SignInManager<Account>>().SignInAsync(user, persistent);
            return;
        }
        if (renewalClaims is null) throw new InvalidOperationException("Identity service did not return a renewed identity.");
        var claims = renewalClaims.Select(claim => new Claim(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer));
        var identity = new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme,
            options.Value.ClaimsIdentity.UserNameClaimType, options.Value.ClaimsIdentity.RoleClaimType);
        var principal = new ClaimsPrincipal(identity);
        await context.SignInAsync(IdentityConstants.ApplicationScheme, principal, new AuthenticationProperties { IsPersistent = persistent });
        context.User = principal;
    }
}

internal sealed class RemoteTwoFactorSettings(HttpClient client)
{
    public async Task<T> SendAsync<T>(string action, TwoFactorSettingsRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/two-factor/settings/" + action, request, token);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty two-factor settings response.");
    }
}
