using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Modules.IdentityAccess.Application;

/// <summary>Validates an API credential against the current identity data.</summary>
public sealed class ApiCredentialVerifier(IdentityAccessDbContext db)
{
    public async Task<VerifiedApiCredential?> VerifyAsync(string value, CancellationToken token = default)
    {
        if (value.Length != 80 || value[36] != '.' || !Guid.TryParseExact(value[..36], "D", out var id)) return null;
        byte[] secret;
        try { secret = WebEncoders.Base64UrlDecode(value[37..]); }
        catch (FormatException) { return null; }
        try
        {
            if (secret.Length != 32) return null;
            var digest = SHA256.HashData(secret);
            var credential = await db.ApiCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt <= DateTimeOffset.UtcNow ||
                !CryptographicOperations.FixedTimeEquals(credential.Verifier, digest)) return null;
            return new(credential.Id, credential.Name, credential.CanWrite);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}

public sealed record VerifiedApiCredential(Guid Id, string Name, bool CanWrite);
