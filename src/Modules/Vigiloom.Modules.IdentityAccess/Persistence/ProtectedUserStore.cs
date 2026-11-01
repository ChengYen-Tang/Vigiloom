using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Modules.IdentityAccess.Persistence;

/// <summary>Encrypts Identity authenticator secrets and recovery codes in the database.</summary>
public sealed class ProtectedUserStore(IdentityAccessDbContext context, IDataProtectionProvider protection, IdentityErrorDescriber describer)
    : UserStore<Account, Role, IdentityAccessDbContext, Guid>(context, describer)
{
    public const string InternalProvider = "[AspNetUserStore]";
    public const string AuthenticatorKeyName = "AuthenticatorKey";
    private static bool IsAuthenticatorSecret(string provider, string name) =>
        provider == InternalProvider && name is AuthenticatorKeyName or "RecoveryCodes";

    public override Task SetTokenAsync(Account user, string loginProvider, string name, string? value, CancellationToken cancellationToken = default)
    {
        if (value is not null && IsAuthenticatorSecret(loginProvider, name))
            value = Protector(user.Id).Protect(value);
        return base.SetTokenAsync(user, loginProvider, name, value, cancellationToken);
    }

    public override async Task<string?> GetTokenAsync(Account user, string loginProvider, string name, CancellationToken cancellationToken = default)
    {
        var value = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        return value is not null && IsAuthenticatorSecret(loginProvider, name) ? Protector(user.Id).Unprotect(value) : value;
    }

    private IDataProtector Protector(Guid userId) => protection.CreateProtector("Vigiloom.Identity.Authenticator.v1", userId.ToString("D"));
}
