using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

/// <summary>Administrative account operations and their transactional audit records.</summary>
public sealed class AccountAdministration(
    IdentityAccessDbContext db,
    UserManager<Account> users,
    IAuthorizationService authorization,
    IOptions<IdentityOptions> identityOptions)
{
    public async Task<AccountPage> ListAsync(ClaimsPrincipal actor, int page = 1, CancellationToken cancellationToken = default)
    {
        var actorId = await EnsureAdministratorAsync(actor);
        await EnsureCurrentAdministratorAsync(actor, actorId);
        const int pageSize = 50;
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, int.MaxValue / pageSize);
        var count = await db.Users.CountAsync(cancellationToken);
        var accounts = await db.Users.AsNoTracking().OrderBy(user => user.UserName).ThenBy(user => user.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(user => new { user.Id, user.UserName }).ToListAsync(cancellationToken);
        var accountIds = accounts.Select(user => user.Id).ToArray();
        var roles = await (from membership in db.UserRoles.AsNoTracking()
                           join role in db.Roles.AsNoTracking() on membership.RoleId equals role.Id
                           where accountIds.Contains(membership.UserId)
                           select new { membership.UserId, role.Name }).ToListAsync(cancellationToken);
        var byAccount = roles.ToLookup(role => role.UserId, role => role.Name ?? "");
        return new AccountPage(accounts.Select(user => new AccountSummary(user.Id, user.UserName ?? "", byAccount[user.Id].ToArray())).ToArray(), count, page, pageSize);
    }

    public async Task<AccountOperationResult> CreateAsync(ClaimsPrincipal actor, string userName, string password, string role,
        CancellationToken cancellationToken = default)
    {
        var actorId = await EnsureAdministratorAsync(actor);
        if (!IsSupportedRole(role)) return AccountOperationResult.InvalidRole;
        await using var transaction = await BeginAccountMutationAsync(cancellationToken);
        await EnsureCurrentAdministratorAsync(actor, actorId);
        var account = new Account { UserName = userName };
        var created = await users.CreateAsync(account, password);
        if (!created.Succeeded) return AccountOperationResult.Failed(created);
        var assigned = await users.AddToRoleAsync(account, role);
        if (!assigned.Succeeded) return AccountOperationResult.Failed(assigned);
        db.AccountAuditEvents.Add(new AccountAuditEvent(actorId, account.Id, "AccountCreated", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AccountOperationResult.Success;
    }

    public async Task<AccountOperationResult> SetRoleAsync(ClaimsPrincipal actor, Guid accountId, string role,
        CancellationToken cancellationToken = default)
    {
        var actorId = await EnsureAdministratorAsync(actor);
        if (!IsSupportedRole(role)) return AccountOperationResult.InvalidRole;
        await using var transaction = await BeginAccountMutationAsync(cancellationToken);
        await EnsureCurrentAdministratorAsync(actor, actorId);
        var account = await users.FindByIdAsync(accountId.ToString());
        if (account is null) return AccountOperationResult.NotFound;
        var current = await users.GetRolesAsync(account);
        if (current.Count == 1 && current[0] == role) return AccountOperationResult.Success;
        if (current.Contains(BuiltInRoles.Administrator) && role != BuiltInRoles.Administrator &&
            !await HasOtherAvailableAdministratorAsync(accountId, cancellationToken))
            return AccountOperationResult.LastAdministrator;
        var removed = await users.RemoveFromRolesAsync(account, current);
        if (!removed.Succeeded) return AccountOperationResult.Failed(removed);
        var assigned = await users.AddToRoleAsync(account, role);
        if (!assigned.Succeeded) return AccountOperationResult.Failed(assigned);
        var refreshed = await users.UpdateSecurityStampAsync(account);
        if (!refreshed.Succeeded) return AccountOperationResult.Failed(refreshed);
        db.AccountAuditEvents.Add(new AccountAuditEvent(actorId, accountId, "AccountRoleChanged", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AccountOperationResult.Success;
    }

    public async Task<AccountOperationResult> DeleteAsync(ClaimsPrincipal actor, Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var actorId = await EnsureAdministratorAsync(actor);
        if (actorId == accountId) return AccountOperationResult.SelfDeletion;
        await using var transaction = await BeginAccountMutationAsync(cancellationToken);
        await EnsureCurrentAdministratorAsync(actor, actorId);
        var account = await users.FindByIdAsync(accountId.ToString());
        if (account is null) return AccountOperationResult.NotFound;
        if (await users.IsInRoleAsync(account, BuiltInRoles.Administrator) &&
            !await HasOtherAvailableAdministratorAsync(accountId, cancellationToken))
            return AccountOperationResult.LastAdministrator;
        var deleted = await users.DeleteAsync(account);
        if (!deleted.Succeeded) return AccountOperationResult.Failed(deleted);
        db.AccountAuditEvents.Add(new AccountAuditEvent(actorId, accountId, "AccountDeleted", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AccountOperationResult.Success;
    }

    private async Task<Guid> EnsureAdministratorAsync(ClaimsPrincipal actor)
    {
        if (!(await authorization.AuthorizeAsync(actor, AccessPolicies.Administrator)).Succeeded ||
            !Guid.TryParse(users.GetUserId(actor), out var actorId) || actorId == Guid.Empty)
            throw new UnauthorizedAccessException("Administrator authorization is required.");
        return actorId;
    }

    private async Task EnsureCurrentAdministratorAsync(ClaimsPrincipal principal, Guid actorId)
    {
        // A request may wait for the lock while another administrator changes or revokes its actor.
        var account = await users.FindByIdAsync(actorId.ToString());
        var stamp = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        if (account is null || stamp is null || stamp != await users.GetSecurityStampAsync(account) ||
            await users.IsLockedOutAsync(account) || !await users.IsInRoleAsync(account, BuiltInRoles.Administrator))
            throw new UnauthorizedAccessException("Administrator authorization is no longer valid.");
    }

    private async Task<IDbContextTransaction> BeginAccountMutationAsync(CancellationToken token)
    {
        var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = 'Vigiloom.Identity.AccountMutation',
                    @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
                IF @result < 0 THROW 50000, 'Could not acquire account mutation lock.', 1;
                """, token);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private Task<bool> HasOtherAvailableAdministratorAsync(Guid excluded, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return (from account in db.Users
                join membership in db.UserRoles on account.Id equals membership.UserId
                join role in db.Roles on membership.RoleId equals role.Id
                where account.Id != excluded && role.NormalizedName == "ADMINISTRATOR" &&
                    (account.LockoutEnd == null || account.LockoutEnd <= now)
                select account.Id).AnyAsync(token);
    }

    private static bool IsSupportedRole(string role) => role is BuiltInRoles.Administrator or BuiltInRoles.ReadOnly;
}

public sealed record AccountSummary(Guid Id, string UserName, IReadOnlyList<string> Roles);
public sealed record AccountPage(IReadOnlyList<AccountSummary> Accounts, int TotalCount, int Page, int PageSize);
public enum AccountOperationStatus { Success, NotFound, InvalidRole, LastAdministrator, SelfDeletion, ValidationFailed }
public sealed record AccountOperationResult(AccountOperationStatus Status, IReadOnlyList<string> Errors)
{
    public static AccountOperationResult Success { get; } = new(AccountOperationStatus.Success, []);
    public static AccountOperationResult NotFound { get; } = new(AccountOperationStatus.NotFound, []);
    public static AccountOperationResult InvalidRole { get; } = new(AccountOperationStatus.InvalidRole, []);
    public static AccountOperationResult LastAdministrator { get; } = new(AccountOperationStatus.LastAdministrator, []);
    public static AccountOperationResult SelfDeletion { get; } = new(AccountOperationStatus.SelfDeletion, []);
    public static AccountOperationResult Failed(IdentityResult result) => new(AccountOperationStatus.ValidationFailed,
        result.Errors.Select(error => error.Description).ToArray());
}
