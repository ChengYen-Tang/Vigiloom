using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

public sealed class PingRetentionSettings(
    IDbContextFactory<NetworkDbContext> contexts,
    PingSettings settings,
    IOperationAuditWriter<OperationAudit> audit,
    IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.PingRetention.Preview.v1");

    public async Task<PingRetentionSnapshot> GetAsync(CancellationToken token)
    {
        await settings.EnsureAsync(token);
        await using var db = await contexts.CreateDbContextAsync(token);
        return Snapshot(await db.PingSettings.AsNoTracking().SingleAsync(x => x.Name == "central", token));
    }

    public async Task<PingRetentionPreview?> PreviewAsync(PingRetentionPreviewRequest input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!PingRetentionLimits.IsValid(input.RawDays, input.RollupDays) || actor == Guid.Empty) throw new ArgumentException("Invalid Ping retention settings.");
        await settings.EnsureAsync(token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var policy = await LockAsync(db, token);
        if (!string.Equals(Convert.ToBase64String(policy.Version), input.Version, StringComparison.Ordinal)) return null;
        var now = await NowAsync(db, token);
        var samples = await PingRetentionEligibility.Samples(db, now.AddDays(-input.RawDays)).LongCountAsync(token);
        var rollups = await PingRetentionEligibility.Rollups(db, now.AddDays(-input.RollupDays)).LongCountAsync(token);
        await tx.CommitAsync(token);
        var proof = protector.Protect(JsonSerializer.Serialize(new PreviewProof(actor, input.RawDays, input.RollupDays, input.Version, now)));
        return new(input.RawDays, input.RollupDays, now, samples, rollups, input.Version, proof);
    }

    public async Task<PingRetentionSnapshot?> UpdateAsync(PingRetentionInput input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!PingRetentionLimits.IsValid(input.RawDays, input.RollupDays) || actor == Guid.Empty) throw new ArgumentException("Invalid Ping retention settings.");
        await settings.EnsureAsync(token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var policy = await LockAsync(db, token);
        var version = Convert.ToBase64String(policy.Version);
        if (!string.Equals(version, input.Version, StringComparison.Ordinal)) return null;
        var now = await NowAsync(db, token);
        if ((input.RawDays < policy.RawRetentionDays || input.RollupDays < policy.RollupRetentionDays) && !ValidProof(input, actor, now))
            throw new ArgumentException("A current preview is required when shortening retention.");
        var previous = PingSettings.Values(policy);
        policy.RawRetentionDays = input.RawDays;
        policy.RollupRetentionDays = input.RollupDays;
        policy.UpdatedAt = now;
        policy.UpdatedBy = actor;
        db.Entry(policy).Property(x => x.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, policy.Id, now, new PingSettingsChanged(previous, PingSettings.Values(policy), version, Convert.ToBase64String(policy.Version))), token);
        await tx.CommitAsync(token);
        return Snapshot(policy);
    }

    private bool ValidProof(PingRetentionInput input, Guid actor, DateTimeOffset now)
    {
        if (input.ConfirmationToken is not { Length: > 0 and <= 4096 }) return false;
        try
        {
            var proof = JsonSerializer.Deserialize<PreviewProof>(protector.Unprotect(input.ConfirmationToken));
            return proof is not null && proof.Actor == actor && proof.RawDays == input.RawDays && proof.RollupDays == input.RollupDays &&
                string.Equals(proof.Version, input.Version, StringComparison.Ordinal) && proof.At.Offset == TimeSpan.Zero &&
                proof.At <= now && now - proof.At <= TimeSpan.FromMinutes(5);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return false; }
    }

    private static Task<PingSetting> LockAsync(NetworkDbContext db, CancellationToken token) =>
        db.PingSettings.FromSqlRaw("SELECT * FROM [network].[PingSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Name] = N'central'").SingleAsync(token);

    private static Task<DateTimeOffset> NowAsync(NetworkDbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);

    private static PingRetentionSnapshot Snapshot(PingSetting policy) =>
        new(policy.RawRetentionDays, policy.RollupRetentionDays, Convert.ToBase64String(policy.Version));

    private sealed record PreviewProof(Guid Actor, int RawDays, int RollupDays, string Version, DateTimeOffset At);
}
