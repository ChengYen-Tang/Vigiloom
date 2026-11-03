using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Modules.IdentityAccess.Application;

/// <summary>Projects account and API credential audit metadata without returning secrets or raw actions.</summary>
public sealed class IdentityAuditFeed(IDbContextFactory<IdentityAccessDbContext> contexts)
{
    public async Task<AuditFeedPage> QueryAsync(AuditFeedSource source, OperationAuditQuery query, CancellationToken token)
    {
        if (!AuditFeedLimits.Valid(query) || source is not (AuditFeedSource.Accounts or AuditFeedSource.ApiCredentials))
            throw new ArgumentException("Invalid audit source or range.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var skip = (query.Page - 1) * AuditFeedLimits.PageSize;
        var page = source == AuditFeedSource.Accounts
            ? await db.AccountAuditEvents.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
                .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId).Skip(skip).Take(AuditFeedLimits.PageSize + 1)
                .Select(x => new Row(x.OccurredAt, x.ActorId, x.SubjectId, x.Action)).ToArrayAsync(token)
            : await db.ApiCredentialAuditEvents.AsNoTracking().Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.To)
                .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId).Skip(skip).Take(AuditFeedLimits.PageSize + 1)
                .Select(x => new Row(x.OccurredAt, x.ActorId, x.CredentialId, x.Action)).ToArrayAsync(token);
        var entries = page.Take(AuditFeedLimits.PageSize).Select(row =>
            new AuditFeedEntry(row.OccurredAt, row.ActorId, row.SubjectId, Map(row.Action), AuditFeedOutcome.Recorded)).ToArray();
        var more = page.Length > AuditFeedLimits.PageSize;
        return new(source, entries, query.Page, more && query.Page < 1000, more && query.Page == 1000);
    }

    private static AuditFeedAction Map(string action) => action switch
    {
        "AccountCreated" => AuditFeedAction.AccountCreated,
        "AccountRenamed" => AuditFeedAction.AccountRenamed,
        "AccountRoleChanged" => AuditFeedAction.AccountRoleChanged,
        "AccountDeleted" => AuditFeedAction.AccountDeleted,
        "PasswordResetIssued" => AuditFeedAction.PasswordResetIssued,
        "PasswordResetCompleted" => AuditFeedAction.PasswordResetCompleted,
        "PasswordChanged" => AuditFeedAction.PasswordChanged,
        "TwoFactorEnrollmentStarted" => AuditFeedAction.TwoFactorEnrollmentStarted,
        "TwoFactorEnabled" => AuditFeedAction.TwoFactorEnabled,
        "TwoFactorDisabled" => AuditFeedAction.TwoFactorDisabled,
        "TwoFactorResetByAdministrator" => AuditFeedAction.TwoFactorResetByAdministrator,
        "InitialAdministratorCreated" => AuditFeedAction.InitialAdministratorCreated,
        "LocalAdministratorRecovery" => AuditFeedAction.LocalAdministratorRecovery,
        "ApiCredentialIssued" => AuditFeedAction.ApiCredentialIssued,
        "ApiCredentialRevoked" => AuditFeedAction.ApiCredentialRevoked,
        _ => AuditFeedAction.Unknown
    };

    private sealed record Row(DateTimeOffset OccurredAt, Guid? ActorId, Guid SubjectId, string Action);
}
