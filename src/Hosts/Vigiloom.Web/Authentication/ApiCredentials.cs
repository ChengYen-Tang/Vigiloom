using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

public sealed class ApiCredentials(IdentityAccessDbContext db, UserManager<Account> users,
    IAuthorizationService authorization, IOptions<IdentityOptions> options)
{
    public async Task<ApiCredentialPage> ListAsync(ClaimsPrincipal actor, int page = 1, CancellationToken token = default)
    {
        await EnsureAdministratorAsync(actor);
        const int pageSize = 50;
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, int.MaxValue / pageSize);
        var count = await db.ApiCredentials.CountAsync(token);
        var credentials = await db.ApiCredentials.AsNoTracking().OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new ApiCredentialSummary(x.Id, x.Name, x.CanWrite, x.CreatedAt, x.ExpiresAt, x.RevokedAt))
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        return new(credentials, count, page, pageSize);
    }

    public async Task<ApiCredentialIssueResult> IssueAsync(ClaimsPrincipal actor, string name, bool canWrite, int days,
        string password, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || days is < 1 or > 365)
            return new(ApiCredentialStatus.InvalidInput, null);
        await using var transaction = await AccountMutationLock.AcquireAsync(db, token);
        var account = await EnsureAdministratorAsync(actor);
        if (!await users.CheckPasswordAsync(account, password)) return new(ApiCredentialStatus.InvalidPassword, null);
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var credential = new ApiCredential(name.Trim(), SHA256.HashData(secretBytes), canWrite,
                DateTimeOffset.UtcNow.AddDays(days), account.Id);
            db.ApiCredentials.Add(credential);
            db.ApiCredentialAuditEvents.Add(new ApiCredentialAuditEvent(account.Id, credential.Id, "ApiCredentialIssued", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(ApiCredentialStatus.Success, $"vlg_{credential.Id:D}.{WebEncoders.Base64UrlEncode(secretBytes)}");
        }
        finally { CryptographicOperations.ZeroMemory(secretBytes); }
    }

    public async Task<ApiCredentialStatus> RevokeAsync(ClaimsPrincipal actor, Guid id, string password, CancellationToken token = default)
    {
        await using var transaction = await AccountMutationLock.AcquireAsync(db, token);
        var account = await EnsureAdministratorAsync(actor);
        if (!await users.CheckPasswordAsync(account, password)) return ApiCredentialStatus.InvalidPassword;
        var credential = await db.ApiCredentials.SingleOrDefaultAsync(x => x.Id == id, token);
        if (credential is null) return ApiCredentialStatus.NotFound;
        if (credential.RevokedAt is null)
        {
            credential.Revoke(DateTimeOffset.UtcNow);
            db.ApiCredentialAuditEvents.Add(new ApiCredentialAuditEvent(account.Id, id, "ApiCredentialRevoked", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return ApiCredentialStatus.Success;
    }

    private async Task<Account> EnsureAdministratorAsync(ClaimsPrincipal principal)
    {
        if (!(await authorization.AuthorizeAsync(principal, AccessPolicies.Administrator)).Succeeded)
            throw new UnauthorizedAccessException();
        var account = await CurrentAccount.FindValidAsync(db, users, principal, options);
        if (account is null || !await users.IsInRoleAsync(account, BuiltInRoles.Administrator))
            throw new UnauthorizedAccessException();
        return account;
    }
}

public enum ApiCredentialStatus { Success, InvalidInput, InvalidPassword, NotFound }
public sealed record ApiCredentialIssueResult(ApiCredentialStatus Status, string? Secret);
public sealed record ApiCredentialSummary(Guid Id, string Name, bool CanWrite, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record ApiCredentialPage(IReadOnlyList<ApiCredentialSummary> Credentials, int TotalCount, int Page, int PageSize);
