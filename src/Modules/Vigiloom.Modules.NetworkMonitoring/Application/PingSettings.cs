using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application;

public sealed class PingSettings(IDbContextFactory<NetworkDbContext> contexts, TimeProvider clock)
{
    public static bool Valid(PingSettingsInput input) => input.IntervalSeconds is >= 10 and <= 3600 && input.TimeoutSeconds is >= 1 and <= 10 &&
        input.TimeoutSeconds < input.IntervalSeconds && input.FailureThreshold is >= 1 and <= 10 && input.SuccessThreshold is >= 1 and <= 10 && input.MaxConcurrency is >= 1 and <= 256;

    public async Task<PingSettingsSnapshot> GetAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var setting = await db.PingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Name == "central", token);
        return Map(setting ?? new PingSetting());
    }

    public async Task EnsureAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (await db.PingSettings.AnyAsync(x => x.Name == "central", token)) return;
        db.PingSettings.Add(new PingSetting { CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { }
    }

    /// <returns>The snapshot saved by this transaction, or null on a version conflict.</returns>
    public async Task<PingSettingsSnapshot?> UpdateAsync(PingSettingsInput input, Guid actor, CancellationToken token)
    {
        if (!Valid(input)) throw new ArgumentException("Invalid Ping settings.", nameof(input));
        if (actor == Guid.Empty) throw new ArgumentException("An administrator is required.", nameof(actor));
        byte[] version;
        try { version = Convert.FromBase64String(input.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var setting = await db.PingSettings.SingleOrDefaultAsync(x => x.Name == "central", token);
        if (setting is null)
        {
            if (version.Length != 0) return null;
            setting = new PingSetting { CreatedAt = clock.GetUtcNow() };
            db.PingSettings.Add(setting);
        }
        else
        {
            if (version.Length != 8) return null;
            db.Entry(setting).Property(x => x.Version).OriginalValue = version;
        }
        setting.IntervalSeconds = input.IntervalSeconds; setting.TimeoutSeconds = input.TimeoutSeconds;
        setting.FailureThreshold = input.FailureThreshold; setting.SuccessThreshold = input.SuccessThreshold;
        setting.MaxConcurrency = input.MaxConcurrency; setting.UpdatedAt = clock.GetUtcNow(); setting.UpdatedBy = actor;
        db.Entry(setting).Property(x => x.UpdatedAt).IsModified = true;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
        var latestDue = clock.GetUtcNow().AddSeconds(input.IntervalSeconds);
        await db.PingTargets.Where(x => x.Enabled && x.DueAt > latestDue)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.DueAt, latestDue), token);
        await transaction.CommitAsync(token);
        return Map(setting);
    }

    public static PingSettingsSnapshot Map(PingSetting setting) => new(setting.IntervalSeconds, setting.TimeoutSeconds, setting.FailureThreshold,
        setting.SuccessThreshold, setting.MaxConcurrency, Convert.ToBase64String(setting.Version));
}
