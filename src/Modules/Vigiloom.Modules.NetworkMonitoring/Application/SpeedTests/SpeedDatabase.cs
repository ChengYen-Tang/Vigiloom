using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

internal static class SpeedDatabase
{
    public static async Task EnsureAsync(IDbContextFactory<NetworkDbContext> contexts, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (await db.SpeedSettings.AnyAsync(x => x.Name == "central", token)) return;
        var now = await NowAsync(db, token);
        var setting = new SpeedSetting { CreatedAt = now, UpdatedAt = now };
        setting.NextAutomaticAt = NextAutomatic(now, setting.IntervalHours);
        db.SpeedSettings.Add(setting);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { }
    }
    public static Task<SpeedSetting> LockAsync(NetworkDbContext db, CancellationToken token) =>
        db.SpeedSettings.FromSqlRaw("SELECT * FROM network.SpeedSettings WITH (UPDLOCK,HOLDLOCK) WHERE Name=N'central'").SingleAsync(token);
    public static async Task<DateTimeOffset> NowAsync(NetworkDbContext db, CancellationToken token)
    {
        return await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
    }
    public static DateTimeOffset NextAutomatic(DateTimeOffset now, int hours) => now.AddHours(hours);
}
