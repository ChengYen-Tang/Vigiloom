using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Retention;

public sealed class AuditRetentionSettings(
    IDbContextFactory<OperationsDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit,
    IDataProtectionProvider protection,
    IEnumerable<IAuditRetentionSource> sources)
{
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.AuditRetention.Preview.v1");

    internal async Task EnsureAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (await db.AuditRetentionSettings.AnyAsync(x => x.Slot == 1, token)) return;
        db.AuditRetentionSettings.Add(new AuditRetentionSetting());
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { }
    }

    public async Task<AuditRetentionSnapshot> GetAsync(CancellationToken token)
    {
        await EnsureAsync(token);
        await using var db = await contexts.CreateDbContextAsync(token);
        return Snapshot(await db.AuditRetentionSettings.AsNoTracking().SingleAsync(x => x.Slot == 1, token));
    }

    public async Task<AuditRetentionPreview?> PreviewAsync(AuditRetentionPreviewRequest input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!AuditRetentionLimits.IsValid(input.Days) || actor == Guid.Empty) throw new ArgumentException("Invalid audit retention settings.");
        await EnsureAsync(token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var policy = await LockAsync(db, token);
        if (!string.Equals(input.Version, Convert.ToBase64String(policy.Version), StringComparison.Ordinal)) return null;
        var now = await NowAsync(db, token);
        var count = await db.AuditEvents.Where(x => x.OccurredAt < now.AddDays(-input.Days)).LongCountAsync(token);
        foreach (var source in sources)
            count = checked(count + await source.CountAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), now.AddDays(-input.Days), token));
        var (episodes, notifications) = await AlertRetention.CountAsync(db, now.AddDays(-input.Days), token);
        await tx.CommitAsync(token);
        var proof = protector.Protect(JsonSerializer.Serialize(new PreviewProof(actor, input.Days, input.Version, now)));
        return new(input.Days, now, count, input.Version, proof, episodes, notifications);
    }

    public async Task<AuditRetentionSnapshot?> UpdateAsync(AuditRetentionInput input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!AuditRetentionLimits.IsValid(input.Days) || actor == Guid.Empty) throw new ArgumentException("Invalid audit retention settings.");
        await EnsureAsync(token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var policy = await LockAsync(db, token);
        var version = Convert.ToBase64String(policy.Version);
        if (!string.Equals(input.Version, version, StringComparison.Ordinal)) return null;
        var now = await NowAsync(db, token);
        if (input.Days < policy.Days && !ValidProof(input, actor, now)) throw new ArgumentException("A current preview is required when shortening retention.");
        var previous = policy.Days;
        policy.Days = input.Days;
        db.Entry(policy).Property(x => x.Days).IsModified = true;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, policy.Id, now, new AuditRetentionChanged(previous, policy.Days, version, Convert.ToBase64String(policy.Version))), token);
        await tx.CommitAsync(token);
        return Snapshot(policy);
    }

    private bool ValidProof(AuditRetentionInput input, Guid actor, DateTimeOffset now)
    {
        if (input.ConfirmationToken is not { Length: > 0 and <= 4096 }) return false;
        try
        {
            var proof = JsonSerializer.Deserialize<PreviewProof>(protector.Unprotect(input.ConfirmationToken));
            return proof is not null && proof.Actor == actor && proof.Days == input.Days &&
                string.Equals(proof.Version, input.Version, StringComparison.Ordinal) && proof.At.Offset == TimeSpan.Zero &&
                proof.At <= now && now - proof.At <= TimeSpan.FromMinutes(5);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return false; }
    }

    internal static Task<AuditRetentionSetting> LockAsync(OperationsDbContext db, CancellationToken token) =>
        db.AuditRetentionSettings.FromSqlRaw("SELECT * FROM [operations].[AuditRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
    internal static Task<DateTimeOffset> NowAsync(OperationsDbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
    private static AuditRetentionSnapshot Snapshot(AuditRetentionSetting policy) => new(policy.Days, Convert.ToBase64String(policy.Version));
    private sealed record PreviewProof(Guid Actor, int Days, string Version, DateTimeOffset At);
}
