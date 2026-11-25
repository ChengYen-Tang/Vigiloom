using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;

namespace Vigiloom.Web.Authentication;

internal sealed class AccountPasswordOperations(IServiceProvider services, IConfiguration configuration, IOptions<IdentityOptions> options)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Identity:Url"]);
    private RemoteAccountPasswordOperations Client => services.GetRequiredService<RemoteAccountPasswordOperations>();
    public Task<InitialAdministratorResult> CreateAsync(string? setupToken, string userName, string password, CancellationToken token = default) =>
        Remote ? Client.SendAsync<InitialAdministratorRequest, InitialAdministratorResult>("initialize", new(setupToken, userName, password), token)
            : services.GetRequiredService<InitialAdministrator>().CreateAsync(setupToken, userName, password, token);
    public Task<bool> RedeemAsync(string userName, string code, string password, CancellationToken token = default) =>
        Remote ? Client.SendAsync<RedeemPasswordRequest, bool>("redeem-password", new(userName, code, password), token)
            : services.GetRequiredService<AccountPasswordReset>().RedeemAsync(userName, code, password, token);
    public Task<AccountOperationResult> ChangeAsync(ClaimsPrincipal principal, string currentPassword, string newPassword, CancellationToken token = default) =>
        Remote ? Client.SendAsync<ChangePasswordRequest, AccountOperationResult>("change-password", new(IdentityActorFactory.Create(principal, options.Value), currentPassword, newPassword), token)
            : services.GetRequiredService<SelfPasswordChange>().ChangeAsync(principal, currentPassword, newPassword, token);
}

internal sealed class RemoteAccountPasswordOperations(HttpClient client)
{
    public async Task<TResponse> SendAsync<TRequest, TResponse>(string action, TRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/accounts/" + action, request, token);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty password operation response.");
    }
}
