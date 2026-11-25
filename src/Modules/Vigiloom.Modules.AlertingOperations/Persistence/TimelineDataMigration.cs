using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Events;
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
        await using (var command = new SqlCommand("""
            SELECT Id, DeviceId, CompletedAt, FilePath, Outcome FROM agent.YaraScans
            WHERE CompletedAt IS NOT NULL ORDER BY RowId
            """, source))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            var options = new DbContextOptionsBuilder<OperationsDbContext>()
                .UseSqlServer(target, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using var db = new OperationsDbContext(options);
            await db.Database.UseTransactionAsync(transaction, token);
            var count = 0;
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetGuid(0); var device = reader.GetGuid(1);
                var outcome = reader.IsDBNull(4) ? "" : reader.GetInt32(4).ToString();
                db.Set<TimelineRecord>().Add(new TimelineRecord
                {
                    MessageId = Guid.CreateVersion7(), Source = "yara", Time = reader.GetDateTimeOffset(2).ToUniversalTime(),
                    ResourceId = device, ResourceKind = "device", Summary = $"YARA 掃描：{reader.GetString(3)}",
                    Evidence = $"掃描 {id:D}；結果 {outcome}", Link = $"/devices/{device:D}/yara-scans?scan={id:D}"
                });
                if (++count % 500 == 0) { await db.SaveChangesAsync(token); db.ChangeTracker.Clear(); }
            }
            if (count % 500 != 0) await db.SaveChangesAsync(token);
        }
        return;

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            await writer.AppendLocalAsync(batch, target, transaction, token);
            batch.Clear();
        }
    }
}
