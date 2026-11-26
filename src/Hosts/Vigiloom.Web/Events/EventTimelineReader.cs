using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Events;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Web.Events;

/// <summary>Combines bounded references from existing host, service and security histories.</summary>
internal sealed class EventTimelineReader(IDbContextFactory<AgentManagementDbContext> agents,
    IDbContextFactory<ServiceObservabilityDbContext> services, IDbContextFactory<SecurityAnalysisDbContext> security,
    IDbContextFactory<ResourcesDbContext> resources, IConfiguration? configuration = null, IServiceProvider? provider = null)
{
    private static readonly string[] Sources = ["host-log", "service-log", "service-trace", "service-health", "inventory", "integrity", "sigma", "vulnerability", "baseline", "ioc", "yara"];

    public async Task<EventTimelinePage> GetAsync(EventTimelineQuery query, CancellationToken token)
    {
        var start = query.From.ToUniversalTime(); var end = query.Until.ToUniversalTime();
        if (end <= start || end - start > TimeSpan.FromDays(31) || query.Page is < 1 or > 20 ||
            query.GroupId is { Version: not 7 } || query.Source is { Length: > 0 } source && !Sources.Contains(source, StringComparer.Ordinal))
            throw new ArgumentException("Invalid event query.");
        if (!string.IsNullOrWhiteSpace(configuration?["Services:Operations:Url"]))
            return await provider!.GetRequiredService<RemoteTimelineReader>().GetAsync(query, token);
        var limit = query.Page * 50 + 1;
        Guid[]? deviceIds = null, serviceIds = null;
        if (query.GroupId is { } group)
        {
            await using var db = await resources.CreateDbContextAsync(token);
            deviceIds = await db.DeviceGroups.AsNoTracking().Where(x => x.GroupId == group).Select(x => x.DeviceId).ToArrayAsync(token);
            serviceIds = await db.ServiceGroups.AsNoTracking().Where(x => x.GroupId == group).Select(x => x.ServiceId).ToArrayAsync(token);
        }
        bool Include(string source) => string.IsNullOrEmpty(query.Source) || query.Source == source;
        var entries = new List<EventTimelineEntry>();
        var serviceLogRows = new Dictionary<EventTimelineEntry, long>(ReferenceEqualityComparer.Instance);
        var hostLogRows = new Dictionary<EventTimelineEntry, long>(ReferenceEqualityComparer.Instance);
        if (Include("host-log") || Include("yara"))
        {
            await using var db = await agents.CreateDbContextAsync(token);
            if (Include("host-log"))
            {
                var rows = await (from log in db.HostLogs.AsNoTracking()
                    join agent in db.Agents.AsNoTracking() on log.AgentId equals agent.Id
                    where log.ReceivedAt >= start && log.ReceivedAt < end && (deviceIds == null || deviceIds.Contains(agent.DeviceId))
                    orderby log.ReceivedAt descending, log.RowId descending
                    select new { log.RowId, agent.DeviceId, log.AgentId, log.MessageId, log.SourceName, log.ParserVersion, log.ReceivedAt, log.GapReason }).Take(limit).ToArrayAsync(token);
                foreach (var x in rows)
                {
                    var entry = new EventTimelineEntry("host-log", x.ReceivedAt, x.DeviceId, "device",
                        x.GapReason is null ? $"主機日誌：{x.SourceName}" : $"主機日誌採集缺口：{x.SourceName}",
                        $"解析版本 {x.ParserVersion}；{x.AgentId:D}/{x.MessageId:D}", $"/devices/{x.DeviceId:D}/host-logs/{x.AgentId:D}/{x.MessageId:D}");
                    entries.Add(entry);
                    hostLogRows.Add(entry, x.RowId);
                }
            }
            if (Include("yara"))
            {
                var rows = await db.YaraScans.AsNoTracking().Where(x => x.CompletedAt != null && x.CompletedAt >= start && x.CompletedAt < end &&
                    (deviceIds == null || deviceIds.Contains(x.DeviceId))).OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.RowId)
                    .Select(x => new { x.DeviceId, x.Id, x.CompletedAt, x.FilePath, x.Outcome }).Take(limit).ToArrayAsync(token);
                entries.AddRange(rows.Select(x => new EventTimelineEntry("yara", x.CompletedAt!.Value, x.DeviceId, "device",
                    $"YARA 掃描：{x.FilePath}", $"掃描 {x.Id:D}；結果 {x.Outcome}", $"/devices/{x.DeviceId:D}/yara-scans?scan={x.Id:D}")));
            }
        }
        if (Include("service-health") || Include("service-log") || Include("service-trace"))
        {
            await using var db = await services.CreateDbContextAsync(token);
            if (Include("service-log"))
            {
                var logs = await db.LogRecords.AsNoTracking().Where(x => x.OccurredAt >= start && x.OccurredAt < end &&
                    (serviceIds == null || serviceIds.Contains(x.ServiceId))).OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
                    .Select(x => new { x.RowId, x.ServiceId, x.InstanceId, x.OccurredAt, x.TraceId }).Take(limit).ToArrayAsync(token);
                foreach (var log in logs)
                {
                    var entry = new EventTimelineEntry("service-log", log.OccurredAt, log.ServiceId, "service",
                        "應用程式日誌", log.TraceId is { Length: > 0 } ? $"Trace {Convert.ToHexString(log.TraceId).ToLowerInvariant()}" : $"服務實例 {log.InstanceId?.ToString("D") ?? "未指定"}", $"/services/{log.ServiceId:D}");
                    entries.Add(entry);
                    serviceLogRows.Add(entry, log.RowId);
                }
            }
            if (Include("service-trace"))
            {
                var spans = await db.TraceSpans.AsNoTracking().Where(x => x.StartedAt >= start && x.StartedAt < end &&
                    (serviceIds == null || serviceIds.Contains(x.ServiceId))).OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.RowId)
                    .Select(x => new { x.ServiceId, x.StartedAt, x.TraceId, x.SpanId }).Take(limit).ToArrayAsync(token);
                entries.AddRange(spans.Select(x => new EventTimelineEntry("service-trace", x.StartedAt, x.ServiceId, "service",
                    "應用程式追蹤", $"Trace {Convert.ToHexStringLower(x.TraceId)}；Span {Convert.ToHexStringLower(x.SpanId)}", $"/services/{x.ServiceId:D}")));
            }
            if (Include("service-health"))
            {
            var rows = await (from observation in db.HealthObservations.AsNoTracking()
                join target in db.HealthTargets.AsNoTracking() on observation.TargetId equals target.Id
                where observation.ReceivedAt >= start && observation.ReceivedAt < end && (serviceIds == null || serviceIds.Contains(target.ServiceId))
                orderby observation.ReceivedAt descending, observation.RowId descending
                select new { target.ServiceId, observation.TargetId, observation.ReceivedAt, observation.Kind }).Take(limit).ToArrayAsync(token);
            entries.AddRange(rows.Select(x => new EventTimelineEntry("service-health", x.ReceivedAt, x.ServiceId, "service",
                $"服務健康檢查：{x.Kind}", $"檢查 {x.TargetId:D}", $"/services/{x.ServiceId:D}")));
            }
        }
        if (Include("sigma") || Include("vulnerability") || Include("baseline") || Include("ioc") || Include("inventory") || Include("integrity"))
        {
            await using var db = await security.CreateDbContextAsync(token);
            if (Include("inventory"))
            {
                var rows = await db.InventoryChanges.AsNoTracking().Where(x => x.ObservedAt >= start && x.ObservedAt < end &&
                    (deviceIds == null || deviceIds.Contains(x.DeviceId))).OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.RowId)
                    .Select(x => new { x.DeviceId, x.AgentId, x.ObservedAt, x.Source, x.Key, x.Kind, x.Generation }).Take(limit).ToArrayAsync(token);
                entries.AddRange(rows.Select(x => new EventTimelineEntry("inventory", x.ObservedAt, x.DeviceId, "device",
                    $"資產盤點變更：{x.Key}", $"來源 {x.Source}；類型 {x.Kind}；Agent {x.AgentId:D}；世代 {x.Generation}", $"/devices/{x.DeviceId:D}")));
            }
            if (Include("integrity"))
            {
                var rows = await db.IntegrityChanges.AsNoTracking().Where(x => x.ObservedAt >= start && x.ObservedAt < end &&
                    (deviceIds == null || deviceIds.Contains(x.DeviceId))).OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.RowId)
                    .Select(x => new { x.DeviceId, x.AgentId, x.ObservedAt, x.ScopeId, x.ScopeRevision, x.Key, x.Kind, x.CoverageGap }).Take(limit).ToArrayAsync(token);
                entries.AddRange(rows.Select(x => new EventTimelineEntry("integrity", x.ObservedAt, x.DeviceId, "device",
                    $"完整性變更：{x.Key}", $"範圍 {x.ScopeId:D}；版本 {x.ScopeRevision}；類型 {x.Kind}；Agent {x.AgentId:D}{(x.CoverageGap ? "；採集有缺口" : "")}", $"/devices/{x.DeviceId:D}")));
            }
            if (Include("sigma"))
            {
                var rows = await (from result in db.SigmaDetectionResults.AsNoTracking()
                    join rule in db.SigmaRuleTests.AsNoTracking() on result.TestId equals rule.Id
                    where result.ReceivedAt >= start && result.ReceivedAt < end && (deviceIds == null || deviceIds.Contains(result.DeviceId))
                    orderby result.ReceivedAt descending, result.RowId descending
                    select new { result.DeviceId, result.AgentId, result.MessageId, result.ReceivedAt, result.Result, rule.Path, rule.RevisionId, result.TestId }).Take(limit).ToArrayAsync(token);
                entries.AddRange(rows.Select(x => new EventTimelineEntry("sigma", x.ReceivedAt, x.DeviceId, "device",
                    x.Result == 1 ? $"行為規則命中：{x.Path}" : $"行為規則未評估：{x.Path}",
                    $"版本 {x.RevisionId:D}；事件 {x.AgentId:D}/{x.MessageId:D}", $"/devices/{x.DeviceId:D}/host-logs/{x.AgentId:D}/{x.MessageId:D}", $"/events/rules/{x.TestId:D}")));
            }
            if (Include("vulnerability"))
            {
                var rows = await (from evaluation in db.VulnerabilityEvaluations.AsNoTracking()
                    join finding in db.VulnerabilityFindings.AsNoTracking() on evaluation.FindingId equals finding.Id
                    where evaluation.EvaluatedAt >= start && evaluation.EvaluatedAt < end && (deviceIds == null || deviceIds.Contains(finding.DeviceId))
                    orderby evaluation.EvaluatedAt descending, evaluation.RowId descending
                    select new { finding.DeviceId, finding.AdvisoryId, evaluation.EvaluatedAt, evaluation.RevisionId, evaluation.State }).Take(limit).ToArrayAsync(token);
                entries.AddRange(rows.Select(x => new EventTimelineEntry("vulnerability", x.EvaluatedAt, x.DeviceId, "device",
                    $"漏洞評估：{x.AdvisoryId}", $"情報版本 {x.RevisionId:D}；狀態 {x.State}", $"/devices/{x.DeviceId:D}?tab=vulnerabilities&search={Uri.EscapeDataString(x.AdvisoryId)}")));
            }
            if (Include("baseline"))
            {
                var rows = await db.BaselineObservations.AsNoTracking().Where(x => x.ReceivedAt >= start && x.ReceivedAt < end &&
                    (deviceIds == null || deviceIds.Contains(x.DeviceId))).OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RowId)
                    .Select(x => new { x.DeviceId, x.ReceivedAt, x.CheckId, x.RevisionId, x.Outcome }).Take(limit).ToArrayAsync(token);
                entries.AddRange(rows.Select(x => new EventTimelineEntry("baseline", x.ReceivedAt, x.DeviceId, "device",
                    $"安全基準檢查：{x.CheckId}", $"版本 {x.RevisionId:D}；結果 {x.Outcome}", $"/devices/{x.DeviceId:D}")));
            }
            if (Include("ioc"))
            {
                var rows = await db.IocDetections.AsNoTracking().Where(x => x.ReceivedAt >= start && x.ReceivedAt < end &&
                    (deviceIds == null || deviceIds.Contains(x.DeviceId))).OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RowId)
                    .Select(x => new { x.DeviceId, x.ReceivedAt, x.AgentId, x.MessageId, x.Kind, x.Value, x.RevisionId }).Take(limit).ToArrayAsync(token);
                entries.AddRange(rows.Select(x => new EventTimelineEntry("ioc", x.ReceivedAt, x.DeviceId, "device",
                    $"IOC 比對線索：{x.Kind} {x.Value}", $"版本 {x.RevisionId:D}；事件 {x.AgentId:D}/{x.MessageId:D}", $"/devices/{x.DeviceId:D}")));
            }
        }
        // LINQ's stable ordering preserves each source's database RowId tie-breaker.
        var ordered = entries.OrderByDescending(x => x.Time).ThenBy(x => x.Source, StringComparer.Ordinal).ToArray();
        var items = ordered.Skip((query.Page - 1) * 50).Take(50).ToArray();
        var selectedHostIds = items.Where(hostLogRows.ContainsKey).Select(x => hostLogRows[x]).ToArray();
        if (selectedHostIds.Length > 0)
        {
            await using var db = await agents.CreateDbContextAsync(token);
            var contents = await db.HostLogs.AsNoTracking().Where(x => selectedHostIds.Contains(x.RowId))
                .Select(x => new { x.RowId, Content = x.Content.Substring(0, 513) })
                .ToDictionaryAsync(x => x.RowId, x => x.Content, token);
            for (var i = 0; i < items.Length; i++)
                if (hostLogRows.TryGetValue(items[i], out var id) && contents.TryGetValue(id, out var content))
                    items[i] = items[i] with { Evidence = $"{items[i].Evidence}；原文：{(content.Length > 512 ? content[..512] + "…" : content)}" };
        }
        var selectedLogIds = items.Where(serviceLogRows.ContainsKey).Select(x => serviceLogRows[x]).ToArray();
        if (selectedLogIds.Length > 0)
        {
            await using var db = await services.CreateDbContextAsync(token);
            var payloads = await db.LogRecords.AsNoTracking().Where(x => selectedLogIds.Contains(x.RowId))
                .Select(x => new { x.RowId, x.RecordPayload }).ToDictionaryAsync(x => x.RowId, x => x.RecordPayload, token);
            for (var i = 0; i < items.Length; i++)
            {
                if (!serviceLogRows.TryGetValue(items[i], out var id) || !payloads.TryGetValue(id, out var payload)) continue;
                var (body, truncated) = ServiceEvidenceReader.FormatLogBody(payload);
                items[i] = items[i] with { Summary = $"應用程式日誌：{body}{(truncated ? "…" : "")}" };
            }
        }
        var hasAdditional = ordered.Length > query.Page * 50;
        return new(items, query.Page, hasAdditional && query.Page < 20, hasAdditional && query.Page == 20);
    }
}
