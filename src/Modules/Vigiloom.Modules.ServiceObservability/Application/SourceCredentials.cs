using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

/// <summary>Issues and validates dedicated application-source credentials. Callers authorize issuance and revocation.</summary>
public sealed class SourceCredentials(IDbContextFactory<ServiceObservabilityDbContext> contexts, TimeProvider clock)
{
    private const string Prefix = "vls_";
    private const int SecretLength = 32;

    internal async Task<SourceCredentialIssue> IssueAsync(string name, CancellationToken token)
    {
        var (source, credential) = Create(name, clock.GetUtcNow());
        await using var db = await contexts.CreateDbContextAsync(token);
        db.Sources.Add(source);
        await db.SaveChangesAsync(token);
        return new(source.Id, credential);
    }

    internal static (ServiceSource Source, string Credential) Create(string name, DateTimeOffset now)
    {
        var secret = RandomNumberGenerator.GetBytes(SecretLength);
        try
        {
            var source = new ServiceSource(name, SHA256.HashData(secret), now);
            return (source, $"{Prefix}{source.Id:D}.{Base64Url(secret)}");
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>Returns the source identity only; write transactions must independently revalidate revocation.</summary>
    public async Task<Guid?> AuthenticateAsync(string? credential, CancellationToken token)
    {
        if (!TryParse(credential, out var sourceId, out var secret)) return null;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var source = await db.Sources.AsNoTracking().Where(x => x.Id == sourceId && x.RevokedAt == null)
                .Select(x => new { x.Id, x.Verifier }).SingleOrDefaultAsync(token);
            if (source is null) return null;
            var verifier = SHA256.HashData(secret);
            return CryptographicOperations.FixedTimeEquals(verifier, source.Verifier) ? source.Id : null;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal async Task<bool> RevokeAsync(Guid sourceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.Sources.SingleOrDefaultAsync(x => x.Id == sourceId, token);
        if (source is null) return false;
        source.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(token);
        return true;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryParse(string? value, out Guid sourceId, out byte[] secret)
    {
        sourceId = default;
        secret = [];
        // 4-character prefix, canonical GUID, separator, and unpadded 32-byte Base64URL.
        if (value is not { Length: 84 } || !value.StartsWith(Prefix, StringComparison.Ordinal) || value[40] != '.' ||
            !Guid.TryParseExact(value.AsSpan(4, 36), "D", out sourceId) || sourceId.Version != 7) return false;
        var encoded = value.AsSpan(41);
        foreach (var c in encoded)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        try
        {
            secret = Convert.FromBase64String(encoded.ToString().Replace('-', '+').Replace('_', '/') + "=");
            if (secret.Length == SecretLength && string.Equals(Base64Url(secret), encoded.ToString(), StringComparison.Ordinal)) return true;
            CryptographicOperations.ZeroMemory(secret);
            secret = [];
            return false;
        }
        catch (FormatException) { return false; }
    }
}

public sealed record SourceCredentialIssue(Guid SourceId, string Secret);
