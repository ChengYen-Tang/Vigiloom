using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

public sealed class SpeedSettings(IDbContextFactory<NetworkDbContext> contexts)
{
    public static bool Valid(SpeedSettingsInput input) => input.IntervalHours is >= 6 and <= 168 && input.MaxMiB is >= 1 and <= 200 &&
        input.TimeoutSeconds is >= 5 and <= 60 && (!input.Automatic || input.Consent) && (!input.Consent || input.ConsentPolicy == SpeedDatabase.ConsentPolicy);
    public async Task<SpeedSettingsSnapshot> GetAsync(CancellationToken token)
    {
        await SpeedDatabase.EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        var setting = await db.SpeedSettings.AsNoTracking().SingleAsync(x => x.Name == "central", token);
        return await SnapshotAsync(db, setting, await SpeedDatabase.NowAsync(db, token), token);
    }
    public async Task<SpeedSettingsSnapshot?> UpdateAsync(SpeedSettingsInput input, Guid actor, CancellationToken token)
    {
        if (!Valid(input) || actor == Guid.Empty) throw new ArgumentException("Invalid settings or administrator.");
        await SpeedDatabase.EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var setting = await SpeedDatabase.LockAsync(db, token);
        if (!string.Equals(Convert.ToBase64String(setting.Version), input.Version, StringComparison.Ordinal)) return null;
        var now = await SpeedDatabase.NowAsync(db, token);
        var changedSchedule = setting.Automatic != input.Automatic || setting.IntervalHours != input.IntervalHours;
        setting.Automatic = input.Automatic;
        setting.IntervalHours = input.IntervalHours; setting.MaxMiB = input.MaxMiB; setting.TimeoutSeconds = input.TimeoutSeconds;
        if (input.Consent && !SpeedDatabase.HasConsent(setting))
        {
            setting.ConsentPolicy = SpeedDatabase.ConsentPolicy; setting.ConsentedAt = now; setting.ConsentedBy = actor;
        }
        if (!input.Consent)
        {
            setting.ConsentPolicy = null; setting.ConsentedAt = null; setting.ConsentedBy = null;
            await db.SpeedRuns.Where(x => x.State == SpeedTestState.Queued).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.State, SpeedTestState.Cancelled).SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.CancellationRequested, true).SetProperty(x => x.DiagnosticCode, "consent_revoked"), token);
            await db.SpeedRuns.Where(x => x.State == SpeedTestState.Running)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.CancellationRequested, true), token);
        }
        if (!input.Automatic)
        {
            await db.SpeedRuns.Where(x => x.Automatic && x.State == SpeedTestState.Queued).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.State, SpeedTestState.Cancelled).SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.CancellationRequested, true).SetProperty(x => x.DiagnosticCode, "automatic_disabled"), token);
            await db.SpeedRuns.Where(x => x.Automatic && x.State == SpeedTestState.Running)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.CancellationRequested, true), token);
        }
        setting.NextAutomaticAt = !input.Automatic ? null : changedSchedule || setting.NextAutomaticAt is null
            ? SpeedDatabase.NextAutomatic(now, input.IntervalHours) : setting.NextAutomaticAt;
        setting.UpdatedAt = now; setting.UpdatedBy = actor;
        db.Entry(setting).Property(x => x.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(token);
        var snapshot = await SnapshotAsync(db, setting, now, token);
        await transaction.CommitAsync(token);
        return snapshot;
    }
    private static async Task<SpeedSettingsSnapshot> SnapshotAsync(NetworkDbContext db, SpeedSetting setting, DateTimeOffset now, CancellationToken token)
    {
        var reservations = db.SpeedRuns.Where(x => x.RequestedAt > now.AddHours(-24));
        var total = await reservations.CountAsync(token);
        var automatic = await reservations.CountAsync(x => x.Automatic, token);
        DateTimeOffset? next = null;
        if (total >= 40) next = (await reservations.MinAsync(x => x.RequestedAt, token)).AddHours(24);
        return new(setting.Automatic, setting.IntervalHours, setting.MaxMiB, setting.TimeoutSeconds, SpeedDatabase.HasConsent(setting),
            SpeedDatabase.ConsentPolicy, Convert.ToBase64String(setting.Version), setting.NextAutomaticAt, total, automatic, next);
    }
}
