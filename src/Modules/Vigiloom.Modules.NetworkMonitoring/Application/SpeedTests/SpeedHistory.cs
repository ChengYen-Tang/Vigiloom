using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

public sealed class SpeedHistory(IDbContextFactory<NetworkDbContext> contexts) : ISpeedHistoryClient
{
    public const int PageSize = 50;
    public const int MaxTrend = 1000;
    public static bool Valid(SpeedHistoryQuery query) => query.To > query.From && query.To - query.From <= TimeSpan.FromDays(31) &&
        query.From.Offset == TimeSpan.Zero && query.To.Offset == TimeSpan.Zero && query.Search?.Length is not > 100 && query.Page is >= 1 and <= 10000 && query.Sort is "time" or "download" or "upload";
    public async Task<SpeedHistoryPage> QueryAsync(SpeedHistoryQuery query, CancellationToken token)
    {
        if (!Valid(query)) throw new ArgumentException("Use a UTC range of at most 31 days and a valid page/sort.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var runs = db.SpeedRuns.AsNoTracking().Where(x => x.RequestedAt >= query.From && x.RequestedAt < query.To);
        if (!string.IsNullOrWhiteSpace(query.Search)) runs = runs.Where(x => x.Server != null && x.Server.Contains(query.Search));
        var total = await runs.CountAsync(token);
        var sorted = (query.Sort, query.Descending) switch
        {
            ("download", false) => runs.OrderBy(x => x.DownloadMbps), ("download", true) => runs.OrderByDescending(x => x.DownloadMbps),
            ("upload", false) => runs.OrderBy(x => x.UploadMbps), ("upload", true) => runs.OrderByDescending(x => x.UploadMbps),
            (_, false) => runs.OrderBy(x => x.RequestedAt), _ => runs.OrderByDescending(x => x.RequestedAt)
        };
        var rows = await sorted.ThenBy(x => x.RowId).Skip((query.Page - 1) * PageSize).Take(PageSize).ToArrayAsync(token);
        var trend = await runs.OrderBy(x => x.RequestedAt).ThenBy(x => x.RowId).Take(MaxTrend + 1).ToArrayAsync(token);
        var earliest = await db.SpeedRuns.MinAsync(x => (DateTimeOffset?)x.RequestedAt, token);
        return new(rows.Select(Map).ToArray(), total, query.Page, PageSize, trend.Take(MaxTrend).Select(Map).ToArray(), trend.Length > MaxTrend, earliest);
    }
    public async Task<SpeedRunDetails?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var run = await db.SpeedRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return run is null ? null : new(Map(run), run.MaxMiB, run.TimeoutSeconds, run.Provider, run.City, run.Country, run.Isp, run.Latitude, run.Longitude, run.DiagnosticCode);
    }
    public static SpeedRunSummary Map(SpeedRun run) => new(run.Id, run.RequestedAt, run.StartedAt, run.CompletedAt, run.Automatic, run.State,
        run.Location, run.Server, run.DownloadMbps, run.UploadMbps, run.LatencyMilliseconds, run.Bytes, run.CancellationRequested);
}
