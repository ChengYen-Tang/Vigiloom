using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Reads private, attributed IOC evidence for a device.</summary>
public sealed class IocDetectionHistory(IDbContextFactory<SecurityAnalysisDbContext> contexts, IDeviceAgentSource devices)
{
    public async Task<IocDetectionPage?> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, int page, CancellationToken token)
    {
        from = from.ToUniversalTime(); until = until.ToUniversalTime();
        if (deviceId.Version != 7 || until <= from || until - from > TimeSpan.FromDays(31) || page is < 1 or > 1000)
            throw new ArgumentException("Invalid detection query.");
        if (await devices.GetAsync(deviceId, token) is null) return null;
        var start = from;
        var end = until;
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await (from match in db.IocDetections.AsNoTracking()
            join revision in db.IntelligenceRevisions.AsNoTracking() on match.RevisionId equals revision.Id
            where match.DeviceId == deviceId && match.ReceivedAt >= start && match.ReceivedAt < end
            orderby match.ReceivedAt descending, match.RowId descending
            select new { Match = match, revision.UpstreamVersion }).Skip((page - 1) * 50).Take(51).ToArrayAsync(token);
        var items = rows.Take(50).Select(row =>
        {
            var root = $"https://github.com/Neo23x0/signature-base/blob/{row.UpstreamVersion}/";
            var path = row.Match.Kind is "ip" or "domain" ? "iocs/c2-iocs.txt" : "iocs/hash-iocs.txt";
            return new IocDetectionView(row.Match.AgentId, row.Match.MessageId, row.Match.SourceId, row.Match.RevisionId,
                row.Match.Kind, row.Match.Value, row.Match.Field, row.Match.SourceLine, root + path + "#L" + row.Match.SourceLine,
                root + "LICENSE", row.Match.OccurredAt, row.Match.ReceivedAt, row.Match.EvaluatedAt);
        }).ToArray();
        return new(items, page, rows.Length > 50 && page < 1000);
    }
}
