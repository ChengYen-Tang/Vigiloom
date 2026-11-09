using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text.Json;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

public sealed class SpeedRetentionSettings(IDbContextFactory<NetworkDbContext> contexts, IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection)
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.SpeedRetention.Preview.v1");

    public async Task<SpeedRetentionSnapshot> GetAsync(CancellationToken token)
    {
        await SpeedDatabase.EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        var setting = await db.SpeedSettings.AsNoTracking().SingleAsync(x => x.Name == "central", token);
        return Snapshot(setting);
    }

    public async Task<SpeedRetentionPreview?> PreviewAsync(int days, string version, Guid actor, CancellationToken token)
    {
        if (!SpeedRetentionLimits.IsValid(days) || actor == Guid.Empty) throw new ArgumentException("Invalid retention period or administrator.");
        await SpeedDatabase.EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var setting = await SpeedDatabase.LockAsync(db, token);
        if (!string.Equals(Convert.ToBase64String(setting.Version), version, StringComparison.Ordinal)) return null;
        var now = await SpeedDatabase.NowAsync(db, token);
        var cutoff = now.AddDays(-days);
        var protectedSince = now.AddHours(-24);
        var count = await db.SpeedRuns.CountAsync(x => x.RequestedAt < cutoff &&
            x.State != SpeedTestState.Queued && x.State != SpeedTestState.Running &&
            (x.StartedAt ?? x.RequestedAt) <= protectedSince, token);
        await transaction.CommitAsync(token);
        var confirmation = protector.Protect(JsonSerializer.Serialize(new PreviewProof(actor, days, version, now)));
        return new(days, now, cutoff, count, version, confirmation);
    }

    public async Task<SpeedRetentionSnapshot?> UpdateAsync(SpeedRetentionInput input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!SpeedRetentionLimits.IsValid(input.Days) || actor == Guid.Empty) throw new ArgumentException("Invalid retention settings or administrator.");
        await SpeedDatabase.EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var setting = await SpeedDatabase.LockAsync(db, token);
        var previousVersion = Convert.ToBase64String(setting.Version);
        if (!string.Equals(previousVersion, input.Version, StringComparison.Ordinal)) return null;
        var now = await SpeedDatabase.NowAsync(db, token);
        if (input.Days < setting.RetentionDays && !ValidProof(input, actor, now))
            throw new ArgumentException("A current preview of the shortened retention period is required.", nameof(input));
        var previousDays = setting.RetentionDays;
        setting.RetentionDays = input.Days;
        setting.UpdatedAt = now;
        setting.UpdatedBy = actor;
        db.Entry(setting).Property(x => x.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(token);
        var currentVersion = Convert.ToBase64String(setting.Version);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, setting.Id, now, new SpeedRetentionChanged(previousDays, setting.RetentionDays, previousVersion, currentVersion)), token);
        await transaction.CommitAsync(token);
        return Snapshot(setting);
    }

    private bool ValidProof(SpeedRetentionInput input, Guid actor, DateTimeOffset now)
    {
        if (input.ConfirmationToken is not { Length: > 0 and <= 4096 }) return false;
        try
        {
            var proof = JsonSerializer.Deserialize<PreviewProof>(protector.Unprotect(input.ConfirmationToken));
            return proof is not null && proof.Actor == actor && proof.Days == input.Days &&
                string.Equals(proof.Version, input.Version, StringComparison.Ordinal) && proof.At.Offset == TimeSpan.Zero &&
                proof.At <= now && now - proof.At <= PreviewLifetime;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return false; }
    }

    private sealed record PreviewProof(Guid Actor, int Days, string Version, DateTimeOffset At);

    private static SpeedRetentionSnapshot Snapshot(Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests.SpeedSetting setting) =>
        new(setting.RetentionDays, Convert.ToBase64String(setting.Version), setting.RetentionDeletedTotal,
            setting.RetentionLastBatchCount, setting.RetentionLastBatchAt);
}
