using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;

public sealed class TelemetryRetentionSettings(IDbContextFactory<ServiceObservabilityDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.TelemetryRetention.Preview.v1");

    public async Task<TelemetryRetentionSnapshot> GetAsync(CancellationToken token)
    {
        await EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        return Snapshot(await db.TelemetryRetentionSettings.AsNoTracking().SingleAsync(x => x.Slot == 1, token));
    }

    public async Task<TelemetryRetentionPreview?> PreviewAsync(TelemetryRetentionPreviewRequest input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TelemetryRetentionLimits.IsValid(input.LogDays, input.TraceDays) || actor == Guid.Empty) throw new ArgumentException("Invalid telemetry retention settings.");
        await EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var policy = await LockAsync(db, token);
        if (!string.Equals(input.Version, Convert.ToBase64String(policy.Version), StringComparison.Ordinal)) return null;
        var rawDays = input.MetricRawDays ?? policy.MetricRawDays;
        var rollupDays = input.MetricRollupDays ?? policy.MetricRollupDays;
        var healthDays = input.HealthDays ?? policy.HealthDays;
        if (!TelemetryRetentionLimits.IsValidMetrics(rawDays, rollupDays) || !TelemetryRetentionLimits.IsValidHealth(healthDays)) throw new ArgumentException("Invalid retention settings.");
        var now = await NowAsync(db, token);
        var logs = await db.LogRecords.LongCountAsync(x => x.OccurredAt < now.AddDays(-input.LogDays), token);
        var spans = await db.TraceSpans.LongCountAsync(x => x.StartedAt < now.AddDays(-input.TraceDays), token);
        var points = await db.MetricPoints.LongCountAsync(x => x.OccurredAt < now.AddDays(-rawDays), token);
        var windows = await db.MetricRollupWindows.LongCountAsync(x => x.WindowStart < now.AddDays(-rollupDays), token);
        var healthCutoff = now.AddDays(-healthDays);
        var intervals = await db.HealthStateIntervals.LongCountAsync(x => x.EndedAt != null && x.EndedAt < healthCutoff, token);
        var observations = await db.HealthObservations.LongCountAsync(x => x.ObservedAt < healthCutoff &&
            !db.HealthSchedules.Any(schedule => schedule.TargetId == x.TargetId && schedule.LastEvaluatedAt == x.ObservedAt), token);
        await tx.CommitAsync(token);
        var proof = protector.Protect(JsonSerializer.Serialize(new PreviewProof(actor, input.LogDays, input.TraceDays, rawDays, rollupDays, healthDays, input.Version, now)));
        return new(input.LogDays, input.TraceDays, now, logs, spans, input.Version, proof, rawDays, rollupDays, points, windows, healthDays, observations, intervals);
    }

    public async Task<TelemetryRetentionSnapshot?> UpdateAsync(TelemetryRetentionInput input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TelemetryRetentionLimits.IsValid(input.LogDays, input.TraceDays) || actor == Guid.Empty) throw new ArgumentException("Invalid telemetry retention settings.");
        await EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var policy = await LockAsync(db, token);
        var version = Convert.ToBase64String(policy.Version);
        if (!string.Equals(input.Version, version, StringComparison.Ordinal)) return null;
        var rawDays = input.MetricRawDays ?? policy.MetricRawDays;
        var rollupDays = input.MetricRollupDays ?? policy.MetricRollupDays;
        var healthDays = input.HealthDays ?? policy.HealthDays;
        if (!TelemetryRetentionLimits.IsValidMetrics(rawDays, rollupDays) || !TelemetryRetentionLimits.IsValidHealth(healthDays)) throw new ArgumentException("Invalid retention settings.");
        var now = await NowAsync(db, token);
        if ((input.LogDays < policy.LogDays || input.TraceDays < policy.TraceDays || rawDays < policy.MetricRawDays || rollupDays < policy.MetricRollupDays || healthDays < policy.HealthDays) &&
            !ValidProof(input, rawDays, rollupDays, healthDays, actor, now))
            throw new ArgumentException("A current preview is required when shortening retention.");
        var previousLogs = policy.LogDays;
        var previousTraces = policy.TraceDays;
        var previousRaw = policy.MetricRawDays;
        var previousRollup = policy.MetricRollupDays;
        var previousHealth = policy.HealthDays;
        policy.LogDays = input.LogDays; policy.TraceDays = input.TraceDays;
        policy.MetricRawDays = rawDays; policy.MetricRollupDays = rollupDays;
        policy.HealthDays = healthDays;
        db.Entry(policy).Property(x => x.LogDays).IsModified = true;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, policy.Id, now, new TelemetryRetentionChanged(previousLogs, previousTraces,
                policy.LogDays, policy.TraceDays, version, Convert.ToBase64String(policy.Version), previousRaw, previousRollup, rawDays, rollupDays, previousHealth, healthDays)), token);
        await tx.CommitAsync(token);
        return Snapshot(policy);
    }

    internal static async Task EnsureAsync(IDbContextFactory<ServiceObservabilityDbContext> contexts, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (await db.TelemetryRetentionSettings.AnyAsync(x => x.Slot == 1, token)) return;
        db.TelemetryRetentionSettings.Add(new TelemetryRetentionSetting());
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { }
    }

    private bool ValidProof(TelemetryRetentionInput input, int rawDays, int rollupDays, int healthDays, Guid actor, DateTimeOffset now)
    {
        if (input.ConfirmationToken is not { Length: > 0 and <= 4096 }) return false;
        try
        {
            var proof = JsonSerializer.Deserialize<PreviewProof>(protector.Unprotect(input.ConfirmationToken));
            return proof is not null && proof.Actor == actor && proof.LogDays == input.LogDays && proof.TraceDays == input.TraceDays &&
                proof.MetricRawDays == rawDays && proof.MetricRollupDays == rollupDays &&
                proof.HealthDays == healthDays &&
                string.Equals(proof.Version, input.Version, StringComparison.Ordinal) && proof.At.Offset == TimeSpan.Zero &&
                proof.At <= now && now - proof.At <= TimeSpan.FromMinutes(5);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return false; }
    }

    private static Task<TelemetryRetentionSetting> LockAsync(ServiceObservabilityDbContext db, CancellationToken token) =>
        db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
    private static Task<DateTimeOffset> NowAsync(ServiceObservabilityDbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
    private static TelemetryRetentionSnapshot Snapshot(TelemetryRetentionSetting setting) =>
        new(setting.LogDays, setting.TraceDays, Convert.ToBase64String(setting.Version), setting.MetricRawDays, setting.MetricRollupDays, setting.HealthDays);
    private sealed record PreviewProof(Guid Actor, int LogDays, int TraceDays, int MetricRawDays, int MetricRollupDays, int HealthDays, string Version, DateTimeOffset At);
}
