using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

/// <summary>Creates the first administrator once, after validating the host-provided bootstrap secret.</summary>
public sealed class InitialAdministrator(
    IdentityAccessDbContext db,
    UserManager<Account> users,
    IConfiguration configuration)
{
    public async Task<InitialAdministratorResult> CreateAsync(
        string? setupToken, string userName, string password, CancellationToken cancellationToken = default)
    {
        var expected = configuration["Vigiloom:SetupToken"];
        if (expected is null || expected.Length < 32 || setupToken is null || !Matches(expected, setupToken))
            return new(InitialAdministratorStatus.InvalidSetupToken, []);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = 'Vigiloom.Identity.InitialAdministrator',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
            IF @result < 0 THROW 50000, 'Could not acquire initial administrator lock.', 1;
            """, cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken) ||
            await db.AccountAuditEvents.AnyAsync(entry => entry.Action == "InitialAdministratorCreated", cancellationToken))
            return new(InitialAdministratorStatus.AlreadyInitialized, []);

        var account = new Account { UserName = userName };
        var created = await users.CreateAsync(account, password);
        if (!created.Succeeded)
            return new(InitialAdministratorStatus.ValidationFailed, created.Errors.Select(error => error.Description).ToArray());
        var assigned = await users.AddToRoleAsync(account, BuiltInRoles.Administrator);
        if (!assigned.Succeeded)
            throw new InvalidOperationException("Failed to assign the built-in Administrator role. Verify that migrations and role initialization ran.");
        db.AccountAuditEvents.Add(new AccountAuditEvent(null, account.Id, "InitialAdministratorCreated", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(InitialAdministratorStatus.Created, []);
    }

    private static bool Matches(string expected, string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}

public enum InitialAdministratorStatus { Created, InvalidSetupToken, AlreadyInitialized, ValidationFailed }
public sealed record InitialAdministratorResult(InitialAdministratorStatus Status, IReadOnlyList<string> Errors);
