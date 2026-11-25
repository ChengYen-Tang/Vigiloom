using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Otlp;
using OtlpLog = OpenTelemetry.Proto.Logs.V1.LogRecord;

namespace Vigiloom.Modules.AlertingOperations.Application.Timeline;

public sealed class TimelineReader(IDbContextFactory<OperationsDbContext> contexts, IDbContextFactory<CentralLogsDbContext> logs)
{
    private static readonly string[] Sources = ["host-log", "service-log", "service-trace", "service-health", "inventory", "integrity", "sigma", "vulnerability", "baseline", "ioc", "yara"];

    public static bool ValidSource(string? source) => source is not null && Sources.Contains(source, StringComparer.Ordinal);

    public static bool ValidSourceKind(string? source, string? kind) => ValidSource(source) &&
        (source is "service-log" or "service-trace" or "service-health" ? kind == "service" : kind == "device");

    public static bool Valid(EventTimelineQuery query) => query.Until > query.From && query.Until - query.From <= TimeSpan.FromDays(31) &&
        query.Page is >= 1 and <= 20 && query.GroupId is null or { Version: 7 } &&
        (string.IsNullOrEmpty(query.Source) || ValidSource(query.Source));

    public async Task<EventTimelinePage> GetAsync(EventTimelineQuery query, GroupMembers? group, CancellationToken token)
    {
        if (!Valid(query) || query.GroupId is not null && group is null) throw new ArgumentException("Invalid timeline query.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var from = query.From.ToUniversalTime();
        var until = query.Until.ToUniversalTime();
        var selected = db.TimelineRecords.AsNoTracking().Where(x => x.Time >= from && x.Time < until);
        if (!string.IsNullOrEmpty(query.Source)) selected = selected.Where(x => x.Source == query.Source);
        if (group is not null)
        {
            var devices = group.DeviceIds.ToArray();
            var services = group.ServiceIds.ToArray();
            selected = selected.Where(x => x.ResourceKind == "device" && devices.Contains(x.ResourceId) ||
                x.ResourceKind == "service" && services.Contains(x.ResourceId));
        }
        var rows = await selected.OrderByDescending(x => x.Time).ThenBy(x => x.Source).ThenByDescending(x => x.RowId)
            .Skip((query.Page - 1) * 50).Take(51)
            .Select(x => new { Entry = new EventTimelineEntry(x.Source, x.Time, x.ResourceId, x.ResourceKind, x.Summary, x.Evidence, x.Link, x.RuleLink), x.LocalLogRowId })
            .ToArrayAsync(token);
        var more = rows.Length > 50;
        var page = rows.Take(50).ToArray();
        var ids = page.Where(x => x.Entry.Source == "service-log" && x.LocalLogRowId is not null).Select(x => x.LocalLogRowId!.Value).ToArray();
        var items = page.Select(x => x.Entry).ToArray();
        if (ids.Length > 0)
        {
            await using var logDb = await logs.CreateDbContextAsync(token);
            var payloads = await logDb.LogRecords.AsNoTracking().Where(x => ids.Contains(x.RowId))
                .Select(x => new { x.RowId, x.RecordPayload }).ToDictionaryAsync(x => x.RowId, x => x.RecordPayload, token);
            for (var i = 0; i < page.Length; i++)
            {
                if (page[i].Entry.Source != "service-log" || page[i].LocalLogRowId is not { } id || !payloads.TryGetValue(id, out var payload)) continue;
                try
                {
                    var (body, truncated) = LogBodyFormatter.Format(OtlpLog.Parser.ParseFrom(payload).Body);
                    items[i] = items[i] with { Summary = $"應用程式日誌：{body}{(truncated ? "…" : "")}" };
                }
                catch (Google.Protobuf.InvalidProtocolBufferException) { }
            }
        }
        return new(items, query.Page, more && query.Page < 20, more && query.Page == 20);
    }
}
