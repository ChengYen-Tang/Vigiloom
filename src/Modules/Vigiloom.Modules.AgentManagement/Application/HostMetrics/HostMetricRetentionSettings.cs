using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

public class HostMetricRetentionSettingsBase<TContext>(IDbContextFactory<TContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection) : IHostMetricRetentionSettings where TContext : DbContext
{
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.HostMetricRetention.Preview.v1");

    public async Task<HostMetricRetentionSnapshot> GetAsync(CancellationToken token)
    {
        await EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        return Snapshot(await db.Set<HostMetricRetentionSetting>().AsNoTracking().SingleAsync(x => x.Slot == 1, token));
    }

    public async Task<HostMetricRetentionPreview?> PreviewAsync(HostMetricRetentionPreviewRequest input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!HostMetricRetentionLimits.IsValid(input.RawDays, input.RollupDays) || actor == Guid.Empty) throw new ArgumentException("Invalid host metric retention settings.");
        await EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var policy = await LockAsync(db, token);
        if (!string.Equals(input.Version, Convert.ToBase64String(policy.Version), StringComparison.Ordinal)) return null;
        var now = await NowAsync(db, token);
        var samples = await HostMetricRetentionEligibility.Samples(db, now.AddDays(-input.RawDays)).LongCountAsync(token);
        var rollups = await HostMetricRetentionEligibility.Rollups(db, now.AddDays(-input.RollupDays)).LongCountAsync(token);
        await tx.CommitAsync(token);
        var proof = protector.Protect(JsonSerializer.Serialize(new PreviewProof(actor, input.RawDays, input.RollupDays, input.Version, now)));
        return new(input.RawDays, input.RollupDays, now, samples, rollups, input.Version, proof);
    }

    public async Task<HostMetricRetentionSnapshot?> UpdateAsync(HostMetricRetentionInput input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!HostMetricRetentionLimits.IsValid(input.RawDays, input.RollupDays) || actor == Guid.Empty) throw new ArgumentException("Invalid host metric retention settings.");
        await EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var policy = await LockAsync(db, token);
        var version = Convert.ToBase64String(policy.Version);
        if (!string.Equals(input.Version, version, StringComparison.Ordinal)) return null;
        var now = await NowAsync(db, token);
        if ((input.RawDays < policy.RawDays || input.RollupDays < policy.RollupDays) && !ValidProof(input, actor, now))
            throw new ArgumentException("A current preview is required when shortening retention.");
        var previousRaw = policy.RawDays;
        var previousRollup = policy.RollupDays;
        policy.RawDays = input.RawDays; policy.RollupDays = input.RollupDays;
        db.Entry(policy).Property(x => x.RawDays).IsModified = true;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, policy.Id, now, new HostMetricRetentionChanged(previousRaw, previousRollup,
                policy.RawDays, policy.RollupDays, version, Convert.ToBase64String(policy.Version))), token);
        await tx.CommitAsync(token);
        return Snapshot(policy);
    }

    internal static async Task EnsureAsync(IDbContextFactory<TContext> contexts, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await EnsureAsync(db, token);
    }

    internal static async Task EnsureAsync(DbContext db, CancellationToken token)
    {
        if (await db.Set<HostMetricRetentionSetting>().AnyAsync(x => x.Slot == 1, token)) return;
        db.Set<HostMetricRetentionSetting>().Add(new HostMetricRetentionSetting());
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { }
    }

    private bool ValidProof(HostMetricRetentionInput input, Guid actor, DateTimeOffset now)
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

    private static Task<HostMetricRetentionSetting> LockAsync(DbContext db, CancellationToken token) =>
        db.Set<HostMetricRetentionSetting>().FromSqlRaw("SELECT * FROM [agent].[HostMetricRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
    private static Task<DateTimeOffset> NowAsync(DbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
    private static HostMetricRetentionSnapshot Snapshot(HostMetricRetentionSetting setting) =>
        new(setting.RawDays, setting.RollupDays, Convert.ToBase64String(setting.Version));
    private sealed record PreviewProof(Guid Actor, int RawDays, int RollupDays, string Version, DateTimeOffset At);
}

public sealed class HostMetricRetentionSettings(IDbContextFactory<AgentManagementDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection)
    : HostMetricRetentionSettingsBase<AgentManagementDbContext>(contexts, audit, protection);

public sealed class HostOwnedMetricRetentionSettings(IDbContextFactory<HostMonitoringDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection)
    : HostMetricRetentionSettingsBase<HostMonitoringDbContext>(contexts, audit, protection);
