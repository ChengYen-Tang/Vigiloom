using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

namespace Vigiloom.Modules.NetworkMonitoring.Application;

public sealed record PingLease(Guid DeviceId, string Target, byte[] DeviceVersion, long Generation);

public sealed class PingStore(IDbContextFactory<NetworkDbContext> contexts, IDevicePingSource devices, TimeProvider clock)
{
    public async Task ReconcileAsync(PingSettingsSnapshot settings, CancellationToken token)
    {
        var batch = new List<PingDeviceSnapshot>(250);
        await foreach (var device in devices.ReadEnabledAsync(token))
        {
            batch.Add(device);
            if (batch.Count == 250) { await ReconcileBatchAsync(batch, settings, token); batch.Clear(); }
        }
        if (batch.Count != 0) await ReconcileBatchAsync(batch, settings, token);
    }

    private async Task ReconcileBatchAsync(List<PingDeviceSnapshot> batch, PingSettingsSnapshot settings, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var ids = batch.Select(x => x.Id).ToArray();
        var targets = batch.SelectMany(x => x.Targets).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await db.PingTargets.Where(x => ids.Contains(x.DeviceId) && (x.Enabled || targets.Contains(x.Target))).ToListAsync(token);
        var lookup = existing.ToDictionary(x => (x.DeviceId, x.Target));
        var byDevice = existing.ToLookup(x => x.DeviceId);
        var now = clock.GetUtcNow();
        foreach (var device in batch)
        {
            foreach (var target in device.Targets)
            {
                if (!lookup.TryGetValue((device.Id, target), out var state))
                {
                    db.PingTargets.Add(new PingTargetState { DeviceId = device.Id, Target = target, DeviceVersion = device.Version,
                        Enabled = true, DueAt = now.AddSeconds(Spread(device.Id, target, settings.IntervalSeconds)) });
                }
                else if (!state.Enabled || !state.DeviceVersion.AsSpan().SequenceEqual(device.Version))
                {
                    state.DeviceVersion = device.Version; state.Enabled = true; state.Generation++;
                    state.LeaseUntil = null; state.DueAt = now; state.State = Reachability.Unknown;
                    state.Successes = state.Failures = 0; state.MeasuredAt = null; state.Outcome = null;
                    state.LatencyMilliseconds = null; state.ResolvedAddress = null;
                }
            }
            foreach (var obsolete in byDevice[device.Id].Where(x => x.Enabled && !device.Targets.Contains(x.Target, StringComparer.Ordinal)))
            {
                obsolete.Enabled = false; obsolete.Generation++; obsolete.LeaseUntil = null;
            }
        }
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { /* Another worker claimed or reconciled a row; rescan next tick. */ }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { /* Concurrent initialization is retried next tick. */ }
    }

    public async Task<IReadOnlyList<PingLease>> ClaimAsync(PingSettingsSnapshot settings, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                ;WITH due AS
                (
                    SELECT TOP (@count) * FROM network.PingTargets WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                    WHERE Enabled = 1 AND DueAt <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                      AND (LeaseUntil IS NULL OR LeaseUntil <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                    ORDER BY DueAt, DeviceId, Target
                )
                UPDATE due SET Generation = Generation + 1,
                    LeaseUntil = DATEADD(second, @lease, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                OUTPUT inserted.DeviceId, inserted.Target, inserted.DeviceVersion, inserted.Generation
                """;
            Add(command, "@count", SqlDbType.Int, settings.MaxConcurrency);
            Add(command, "@lease", SqlDbType.Int, 2 * settings.TimeoutSeconds + 30);
            var leases = new List<PingLease>(settings.MaxConcurrency);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) leases.Add(new(reader.GetGuid(0), reader.GetString(1), (byte[])reader.GetValue(2), reader.GetInt64(3)));
            return leases;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    public async Task<bool> IsCurrentAsync(PingLease lease, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        if (!await db.PingTargets.FromSqlInterpolated($"SELECT * FROM [network].[PingTargets] WHERE [DeviceId] = {lease.DeviceId} AND [Target] = {lease.Target} AND [Generation] = {lease.Generation} AND [Enabled] = 1 AND [LeaseUntil] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')").AsNoTracking().AnyAsync(token)) return false;
        return await devices.IsCurrentAsync(lease.DeviceId, lease.DeviceVersion, lease.Target, db.Database.GetDbConnection(), tx.GetDbTransaction(), token);
    }

    public async Task DeactivateAsync(PingLease lease, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.PingTargets.Where(x => x.DeviceId == lease.DeviceId && x.Target == lease.Target && x.Generation == lease.Generation)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, false).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), token);
    }

    public async Task<bool> CommitAsync(PingLease lease, PingSettingsSnapshot settings, PingMeasurement measurement,
        DateTimeOffset measuredAt, string location, CancellationToken token)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try { return await CommitOnceAsync(lease, settings, measurement, measuredAt, location, token); }
            catch (Exception ex) when (attempt < maxAttempts && IsDeadlock(ex))
            {
                // SQL Server has rolled back the victim transaction. Retry from a fresh context
                // and revalidate the lease, device, and settings before writing the same result.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 51) * attempt), token);
            }
        }
    }

    private static bool IsDeadlock(Exception ex)
    {
        // EF Core can wrap a transient SqlException in InvalidOperationException or DbUpdateException.
        for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
            if (cause is SqlException { Number: 1205 }) return true;
        return false;
    }

    private async Task<bool> CommitOnceAsync(PingLease lease, PingSettingsSnapshot settings, PingMeasurement measurement,
        DateTimeOffset measuredAt, string location, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var current = await db.PingSettings.FromSqlRaw("SELECT * FROM [network].[PingSettings] WITH (HOLDLOCK) WHERE [Name] = N'central'").SingleAsync(token);
        if (Convert.ToBase64String(current.Version) != settings.Version) return false;
        var state = await db.PingTargets.FromSqlInterpolated($"SELECT * FROM [network].[PingTargets] WITH (UPDLOCK, HOLDLOCK) WHERE [DeviceId] = {lease.DeviceId} AND [Target] = {lease.Target} AND [Generation] = {lease.Generation} AND [Enabled] = 1 AND [LeaseUntil] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')").SingleOrDefaultAsync(token);
        if (state is null) return false;
        if (!await devices.IsCurrentAsync(lease.DeviceId, lease.DeviceVersion, lease.Target, db.Database.GetDbConnection(), tx.GetDbTransaction(), token)) return false;
        var previous = state.State;
        if (IsStale(state.MeasuredAt, measuredAt, settings.IntervalSeconds))
        {
            state.State = Reachability.Unknown;
            state.Successes = state.Failures = 0;
        }
        PingStateMachine.Apply(state, measurement, settings);
        state.MeasuredAt = measuredAt.ToUniversalTime(); state.Location = location;
        var receivedAt = await DatabaseNowAsync(db, token);
        state.DueAt = receivedAt.AddSeconds(settings.IntervalSeconds);
        var sample = new PingSample(lease.DeviceId, lease.Target, measurement.Address, location, lease.Generation,
            measuredAt, receivedAt, measurement.Outcome, measurement.LatencyMilliseconds);
        db.PingSamples.Add(sample);
        await PingRollupWriter.AddAsync(db, sample, token);
        if (previous != state.State)
            db.PingStateEvents.Add(new PingStateEvent { DeviceId = lease.DeviceId, Target = lease.Target, Generation = lease.Generation,
                OccurredAt = measuredAt.ToUniversalTime(), Previous = previous, Current = state.State });
        await db.SaveChangesAsync(token);
        await using var finish = db.Database.GetDbConnection().CreateCommand();
        finish.Transaction = tx.GetDbTransaction();
        finish.CommandText = """
            UPDATE network.PingTargets SET LeaseUntil = NULL
            WHERE DeviceId = @id AND Target = @target AND Generation = @generation
              AND LeaseUntil > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            """;
        Add(finish, "@id", SqlDbType.UniqueIdentifier, lease.DeviceId);
        Add(finish, "@target", SqlDbType.NVarChar, lease.Target, 253);
        Add(finish, "@generation", SqlDbType.BigInt, lease.Generation);
        if (await finish.ExecuteNonQueryAsync(token) != 1) return false;
        await tx.CommitAsync(token);
        return true;
    }

    public async Task<DevicePingStatus> GetStatusAsync(Guid id, IReadOnlyList<string> targets, string deviceVersion, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.PingTargets.AsNoTracking().Where(x => x.DeviceId == id && x.Enabled && targets.Contains(x.Target)).ToListAsync(token);
        var settings = await db.PingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Name == "central", token);
        return MapStatus(id, targets, deviceVersion, rows, settings, clock.GetUtcNow());
    }

    public async Task<IReadOnlyDictionary<Guid, DevicePingStatus>> GetStatusesAsync(IReadOnlyList<PingDeviceSnapshot> devicesToRead, CancellationToken token)
    {
        if (devicesToRead.Count > 250) throw new ArgumentOutOfRangeException(nameof(devicesToRead));
        await using var db = await contexts.CreateDbContextAsync(token);
        var ids = devicesToRead.Select(x => x.Id).ToArray();
        var targets = devicesToRead.SelectMany(x => x.Targets).Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.PingTargets.AsNoTracking().Where(x => ids.Contains(x.DeviceId) && x.Enabled && targets.Contains(x.Target)).ToListAsync(token);
        var settings = await db.PingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Name == "central", token);
        var lookup = rows.ToLookup(x => x.DeviceId);
        var now = clock.GetUtcNow();
        return devicesToRead.ToDictionary(x => x.Id, x => MapStatus(x.Id, x.Targets, Convert.ToBase64String(x.Version), lookup[x.Id], settings, now));
    }

    private static DevicePingStatus MapStatus(Guid id, IReadOnlyList<string> targets, string deviceVersion, IEnumerable<PingTargetState> rows, PingSetting? settings, DateTimeOffset now)
    {
        var byTarget = rows.ToDictionary(x => x.Target, StringComparer.Ordinal);
        var result = new List<PingTargetStatus>(targets.Count);
        foreach (var target in targets)
        {
            byTarget.TryGetValue(target, out var state);
            if (state is null || !state.Enabled || Convert.ToBase64String(state.DeviceVersion) != deviceVersion)
            {
                result.Add(new(target, null, "central", Reachability.Unknown, "尚無量測資料", null, null, null, false));
                continue;
            }
            var stale = IsStale(state.MeasuredAt, now, settings?.IntervalSeconds ?? 30);
            var display = state.MeasuredAt is null ? "尚無量測資料" : stale ? "資料過期" : state.Outcome switch
            {
                PingOutcome.DnsError => "DNS 解析失敗",
                PingOutcome.PermissionDenied => "監測器權限不足",
                PingOutcome.MonitorError => "監測器錯誤",
                _ => state.State switch { Reachability.Reachable => "可達", Reachability.Unreachable => "不可達", _ => "待確認" }
            };
            result.Add(new(target, state.ResolvedAddress, state.Location, stale ? Reachability.Unknown : state.State, display,
                state.Outcome, state.LatencyMilliseconds, state.MeasuredAt, stale));
        }
        return new(id, result, new(settings?.IntervalSeconds ?? 30, settings?.TimeoutSeconds ?? 2, settings?.FailureThreshold ?? 3, settings?.SuccessThreshold ?? 2));
    }

    private static bool IsStale(DateTimeOffset? measuredAt, DateTimeOffset now, int intervalSeconds) =>
        measuredAt is { } time && now - time > TimeSpan.FromSeconds(Math.Max(90, 2 * intervalSeconds));

    private static int Spread(Guid id, string target, int interval)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{id:D}/{target}"));
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % (uint)interval);
    }
    private static async Task<DateTimeOffset> DatabaseNowAsync(NetworkDbContext db, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')";
        return (DateTimeOffset)(await command.ExecuteScalarAsync(token))!;
    }
    private static void Add(System.Data.Common.DbCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        var parameter = new SqlParameter(name, type) { Value = value };
        if (size > 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }
}
