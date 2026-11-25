using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Services;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;
using Vigiloom.Modules.AlertingOperations.Domain;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

internal static class TimelineDataMigration
{
    internal static async Task CopyLogsAsync(SqlConnection source, SqlConnection target, SqlTransaction transaction, CancellationToken token)
    {
        await using (var check = new SqlCommand("SELECT TOP (1) 1 FROM operations.TimelineRecords", target, transaction))
            if (await check.ExecuteScalarAsync(token) is not null)
                throw new InvalidOperationException("Target timeline must be empty before copying.");

        var writer = new TimelineWriter();
        var batch = new List<(EventTimelineEntry Entry, long LogRowId)>(500);
        await using (var command = new SqlCommand("""
            SELECT h.RowId, a.DeviceId, h.AgentId, h.MessageId, h.ReceivedAt, h.SourceName,
                   h.ParserVersion, h.GapReason, LEFT(h.Content, 513)
            FROM agent.HostLogs AS h INNER JOIN agent.Agents AS a ON h.AgentId = a.Id
            ORDER BY h.RowId
            """, source))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var device = reader.GetGuid(1); var agent = reader.GetGuid(2); var message = reader.GetGuid(3);
                var content = reader.GetString(8);
                batch.Add((new("host-log", reader.GetDateTimeOffset(4), device, "device",
                    reader.IsDBNull(7) ? $"主機日誌：{reader.GetString(5)}" : $"主機日誌採集缺口：{reader.GetString(5)}",
                    $"解析版本 {reader.GetInt32(6)}；{agent:D}/{message:D}；原文：{(content.Length > 512 ? content[..512] + "…" : content)}",
                    $"/devices/{device:D}/host-logs/{agent:D}/{message:D}"), reader.GetInt64(0)));
                if (batch.Count == 500) await FlushAsync();
            }
        }
        await FlushAsync();
        await using (var command = new SqlCommand("SELECT RowId, ServiceId, InstanceId, OccurredAt, TraceId FROM service.LogRecords ORDER BY RowId", source))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var service = reader.GetGuid(1);
                var instance = reader.IsDBNull(2) ? "未指定" : reader.GetGuid(2).ToString("D");
                var evidence = reader.IsDBNull(4) ? $"服務實例 {instance}" : $"Trace {Convert.ToHexStringLower((byte[])reader.GetValue(4))}";
                batch.Add((new("service-log", reader.GetDateTimeOffset(3), service, "service", "應用程式日誌", evidence,
                    $"/services/{service:D}"), reader.GetInt64(0)));
                if (batch.Count == 500) await FlushAsync();
            }
        }
        await FlushAsync();
        await CopyEventsAsync("""
            SELECT Id, DeviceId, CompletedAt, FilePath, Outcome FROM agent.YaraScans
            WHERE CompletedAt IS NOT NULL ORDER BY RowId
            """, reader =>
        {
            var id = reader.GetGuid(0); var device = reader.GetGuid(1);
            var outcome = reader.IsDBNull(4) ? "" : reader.GetInt32(4).ToString();
            return new("yara", reader.GetDateTimeOffset(2), device, "device", $"YARA 掃描：{reader.GetString(3)}",
                $"掃描 {id:D}；結果 {outcome}", $"/devices/{device:D}/yara-scans?scan={id:D}");
        });
        await CopyEventsAsync("SELECT ServiceId, StartedAt, TraceId, SpanId FROM service.TraceSpans ORDER BY RowId", reader =>
        {
            var service = reader.GetGuid(0);
            return new("service-trace", reader.GetDateTimeOffset(1), service, "service", "應用程式追蹤",
                $"Trace {Convert.ToHexStringLower((byte[])reader.GetValue(2))}；Span {Convert.ToHexStringLower((byte[])reader.GetValue(3))}", $"/services/{service:D}");
        });
        await CopyEventsAsync("""
            SELECT t.ServiceId, o.ReceivedAt, o.TargetId, o.Kind FROM service.HealthObservations AS o
            INNER JOIN service.HealthTargets AS t ON o.TargetId = t.Id ORDER BY o.RowId
            """, reader =>
        {
            var service = reader.GetGuid(0); var targetId = reader.GetGuid(2);
            return new("service-health", reader.GetDateTimeOffset(1), service, "service",
                $"服務健康檢查：{(HealthCheckObservationKind)reader.GetByte(3)}", $"檢查 {targetId:D}", $"/services/{service:D}");
        });
        await CopyEventsAsync("SELECT DeviceId, AgentId, ObservedAt, Source, [Key], Kind, Generation FROM security.InventoryChanges ORDER BY RowId", reader =>
        {
            var device = reader.GetGuid(0); var agent = reader.GetGuid(1);
            return new("inventory", reader.GetDateTimeOffset(2), device, "device", $"資產盤點變更：{reader.GetString(4)}",
                $"來源 {(InventorySource)Convert.ToInt32(reader.GetValue(3))}；類型 {reader.GetInt32(5)}；Agent {agent:D}；世代 {reader.GetInt64(6)}", $"/devices/{device:D}");
        });
        await CopyEventsAsync("SELECT DeviceId, AgentId, ObservedAt, ScopeId, ScopeRevision, [Key], Kind, CoverageGap FROM security.IntegrityChanges ORDER BY RowId", reader =>
        {
            var device = reader.GetGuid(0); var agent = reader.GetGuid(1); var scope = reader.GetGuid(3);
            return new("integrity", reader.GetDateTimeOffset(2), device, "device", $"完整性變更：{reader.GetString(5)}",
                $"範圍 {scope:D}；版本 {reader.GetInt64(4)}；類型 {reader.GetInt32(6)}；Agent {agent:D}{(reader.GetBoolean(7) ? "；採集有缺口" : "")}", $"/devices/{device:D}");
        });
        await CopyEventsAsync("""
            SELECT r.DeviceId, r.AgentId, r.MessageId, r.ReceivedAt, r.Result, t.Path, t.RevisionId, r.TestId
            FROM security.SigmaDetectionResults AS r INNER JOIN security.SigmaRuleTests AS t ON r.TestId = t.Id ORDER BY r.RowId
            """, reader =>
        {
            var device = reader.GetGuid(0); var agent = reader.GetGuid(1); var message = reader.GetGuid(2);
            var revision = reader.GetGuid(6); var test = reader.GetGuid(7);
            return new("sigma", reader.GetDateTimeOffset(3), device, "device",
                Convert.ToInt32(reader.GetValue(4)) == 1 ? $"行為規則命中：{reader.GetString(5)}" : $"行為規則未評估：{reader.GetString(5)}",
                $"版本 {revision:D}；事件 {agent:D}/{message:D}", $"/devices/{device:D}/host-logs/{agent:D}/{message:D}", $"/events/rules/{test:D}");
        });
        await CopyEventsAsync("""
            SELECT f.DeviceId, f.AdvisoryId, e.EvaluatedAt, e.RevisionId, e.State FROM security.VulnerabilityEvaluations AS e
            INNER JOIN security.VulnerabilityFindings AS f ON e.FindingId = f.Id ORDER BY e.RowId
            """, reader =>
        {
            var device = reader.GetGuid(0); var advisory = reader.GetString(1); var revision = reader.GetGuid(3);
            return new("vulnerability", reader.GetDateTimeOffset(2), device, "device", $"漏洞評估：{advisory}",
                $"情報版本 {revision:D}；狀態 {reader.GetInt32(4)}", $"/devices/{device:D}?tab=vulnerabilities&search={Uri.EscapeDataString(advisory)}");
        });
        await CopyEventsAsync("SELECT DeviceId, ReceivedAt, CheckId, RevisionId, Outcome FROM security.BaselineObservations ORDER BY RowId", reader =>
        {
            var device = reader.GetGuid(0); var revision = reader.GetGuid(3);
            return new("baseline", reader.GetDateTimeOffset(1), device, "device", $"安全基準檢查：{reader.GetString(2)}",
                $"版本 {revision:D}；結果 {(BaselineOutcome)Convert.ToInt32(reader.GetValue(4))}", $"/devices/{device:D}");
        });
        await CopyEventsAsync("SELECT DeviceId, ReceivedAt, AgentId, MessageId, Kind, Value, RevisionId FROM security.IocDetections ORDER BY RowId", reader =>
        {
            var device = reader.GetGuid(0); var agent = reader.GetGuid(2); var message = reader.GetGuid(3); var revision = reader.GetGuid(6);
            return new("ioc", reader.GetDateTimeOffset(1), device, "device", $"IOC 比對線索：{reader.GetString(4)} {reader.GetString(5)}",
                $"版本 {revision:D}；事件 {agent:D}/{message:D}", $"/devices/{device:D}");
        });
        return;

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            await writer.AppendLocalAsync(batch, target, transaction, token);
            batch.Clear();
        }

        async Task CopyEventsAsync(string sql, Func<SqlDataReader, EventTimelineEntry> map)
        {
            var options = new DbContextOptionsBuilder<OperationsDbContext>()
                .UseSqlServer(target, settings => settings.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using var db = new OperationsDbContext(options);
            await db.Database.UseTransactionAsync(transaction, token);
            await using var command = new SqlCommand(sql, source);
            await using var reader = await command.ExecuteReaderAsync(token);
            var count = 0;
            while (await reader.ReadAsync(token))
            {
                var entry = map(reader);
                db.Set<TimelineRecord>().Add(new TimelineRecord { MessageId = Guid.CreateVersion7(), Source = entry.Source,
                    Time = entry.Time.ToUniversalTime(), ResourceId = entry.ResourceId, ResourceKind = entry.ResourceKind,
                    Summary = entry.Summary, Evidence = entry.Evidence, Link = entry.Link, RuleLink = entry.RuleLink });
                if (++count % 500 == 0) { await db.SaveChangesAsync(token); db.ChangeTracker.Clear(); }
            }
            if (count % 500 != 0) await db.SaveChangesAsync(token);
        }
    }
}
