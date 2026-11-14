using System.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

public enum AdvisoryCredentialResult { Updated, Unchanged, NotFound, Conflict }

/// <summary>Manages a protected optional GitHub token without returning it to callers.</summary>
public sealed class AdvisoryCredentials(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection, TimeProvider clock)
{
    internal const string ProtectionPurpose = "Vigiloom.Intelligence.GitHubCredential.v1";

    public static bool Valid(AdvisoryCredentialUpdate? input) => input is { Version.Length: <= 32 } &&
        (input.Clear ? input.Credential is null : input.Credential is { Length: > 0 and <= 4096 } &&
            input.Credential.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '='));

    public async Task<AdvisoryCredentialResult> UpdateAsync(Guid sourceId, AdvisoryCredentialUpdate input, Guid actorId, CancellationToken token)
    {
        if (sourceId == Guid.Empty || actorId == Guid.Empty || !Valid(input)) return AdvisoryCredentialResult.Conflict;
        byte[] version;
        try { version = Convert.FromBase64String(input.Version); }
        catch (FormatException) { return AdvisoryCredentialResult.Conflict; }
        if (version.Length != 8) return AdvisoryCredentialResult.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources
            .FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null || source.Key != "github-reviewed") return AdvisoryCredentialResult.NotFound;
        if (!source.Version.AsSpan().SequenceEqual(version)) return AdvisoryCredentialResult.Conflict;
        if (input.Clear && source.ProtectedCredential is null) return AdvisoryCredentialResult.Unchanged;
        source.ProtectedCredential = input.Clear ? null : protection.CreateProtector(ProtectionPurpose).Protect(input.Credential!);
        source.LastCheckedAt = null;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actorId, sourceId, clock.GetUtcNow().ToUniversalTime(), new AdvisoryCredentialChanged(!input.Clear)), token);
        await transaction.CommitAsync(token);
        return AdvisoryCredentialResult.Updated;
    }
}
