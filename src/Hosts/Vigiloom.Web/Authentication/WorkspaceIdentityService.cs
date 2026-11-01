using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

internal sealed class WorkspaceIdentityService(IServiceScopeFactory scopes)
{
    public async Task<WorkspaceIdentity?> GetAsync(ClaimsPrincipal principal, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>();
        var account = await CurrentAccount.FindValidAsync(db, users, principal, options);
        token.ThrowIfCancellationRequested();
        if (account is null) return null;
        var roles = await users.GetRolesAsync(account);
        token.ThrowIfCancellationRequested();
        if (!roles.Contains(BuiltInRoles.Administrator) && !roles.Contains(BuiltInRoles.ReadOnly)) return null;
        return new WorkspaceIdentity(account.UserName ?? "", roles.ToArray());
    }
}
