using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;

namespace Vigiloom.Web.Authentication;

internal sealed class AccountManagement(IServiceProvider services, IConfiguration configuration, IOptions<IdentityOptions> options)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Identity:Url"]);
    private AccountAdministration Local => services.GetRequiredService<AccountAdministration>();
    private RemoteAccountManagement Client => services.GetRequiredService<RemoteAccountManagement>();
    private AccountManagementRequest Request(ClaimsPrincipal actor, Guid id = default, int page = 1, string? userName = null, string? role = null, string? password = null) =>
        new(IdentityActorFactory.Create(actor, options.Value), id, page, userName, role, password);

    public Task<AccountPage> ListAsync(ClaimsPrincipal actor, int page = 1, CancellationToken token = default) =>
        Remote ? Client.SendAsync<AccountPage>("list", Request(actor, page: page), token) : Local.ListAsync(actor, page, token);
    public Task<AccountSummary?> GetAsync(ClaimsPrincipal actor, Guid id, CancellationToken token = default) =>
        Remote ? Client.GetAsync(Request(actor, id), token) : Local.GetAsync(actor, id, token);
    public Task<ResetCodeResult> CreateAsync(ClaimsPrincipal actor, string userName, string role, CancellationToken token = default) =>
        Remote ? Client.SendAsync<ResetCodeResult>("create", Request(actor, userName: userName, role: role), token) : Local.CreateAsync(actor, userName, role, token);
    public Task<AccountOperationResult> RenameAsync(ClaimsPrincipal actor, Guid id, string userName, CancellationToken token = default) =>
        Remote ? Client.SendAsync<AccountOperationResult>("rename", Request(actor, id, userName: userName), token) : Local.RenameAsync(actor, id, userName, token);
    public Task<AccountOperationResult> SetRoleAsync(ClaimsPrincipal actor, Guid id, string role, CancellationToken token = default) =>
        Remote ? Client.SendAsync<AccountOperationResult>("role", Request(actor, id, role: role), token) : Local.SetRoleAsync(actor, id, role, token);
    public Task<AccountOperationResult> DeleteAsync(ClaimsPrincipal actor, Guid id, CancellationToken token = default) =>
        Remote ? Client.SendAsync<AccountOperationResult>("delete", Request(actor, id), token) : Local.DeleteAsync(actor, id, token);
    public Task<ResetCodeResult> IssuePasswordResetAsync(ClaimsPrincipal actor, Guid id, CancellationToken token = default) =>
        Remote ? Client.SendAsync<ResetCodeResult>("reset-password", Request(actor, id), token) : Local.IssuePasswordResetAsync(actor, id, token);
    public Task<AccountOperationResult> ResetTwoFactorAsync(ClaimsPrincipal actor, Guid id, string password, CancellationToken token = default) =>
        Remote ? Client.SendAsync<AccountOperationResult>("reset-two-factor", Request(actor, id, password: password), token) : Local.ResetTwoFactorAsync(actor, id, password, token);
}

internal sealed class RemoteAccountManagement(HttpClient client)
{
    public async Task<AccountSummary?> GetAsync(AccountManagementRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/accounts/get", request, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<AccountSummary>(response, token);
    }
    public async Task<T> SendAsync<T>(string action, AccountManagementRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/accounts/" + action, request, token);
        return await ReadAsync<T>(response, token);
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty account management response.");
    }
}
