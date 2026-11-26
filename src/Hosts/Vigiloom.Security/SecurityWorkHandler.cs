using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.SecurityAnalysis.Application.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;

namespace Vigiloom.Security;

/// <summary>Consumes Host batches and commits security effects with the delivery receipt.</summary>
public sealed class SecurityWorkHandler(IConfiguration configuration, IHttpClientFactory clients, IEnumerable<IOutboxHandler>? handlers = null) : IServiceWorkHandler
{
    public async Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
    {
        if (work.Kind is HostLogDispatch.MessageType or HostLogDispatch.IocMessageType or HostLogDispatch.CombinedMessageType)
        {
            var connectionString = configuration.GetConnectionString("Security") ?? throw new InvalidOperationException("ConnectionStrings:Security is required.");
            var registered = (handlers ?? []).ToDictionary(handler => handler.MessageType, StringComparer.Ordinal);
            var consumer = new TransactionalWorkHandler(connectionString, async (item, sql, transaction, token) =>
            {
                var kinds = item.Kind is HostLogDispatch.CombinedMessageType or HostLogDispatch.MessageType
                    ? new[] { HostLogDispatch.MessageType, HostLogDispatch.IocMessageType } : [item.Kind];
                foreach (var kind in kinds)
                {
                    if (!registered.TryGetValue(kind, out var handler)) throw new InvalidOperationException("Security log handler is missing.");
                    await handler.HandleAsync(new OutboxMessage(Guid.Parse(item.DeliveryId!), kind, item.Payload), sql, transaction, token);
                }
                return null;
            });
            return await consumer.ExecuteAsync(work, cancellationToken);
        }
        if (work.Kind != nameof(HostSecurityBatchReady)) throw new InvalidOperationException("Unknown security work type.");
        var batch = JsonSerializer.Deserialize<HostSecurityBatchReady>(work.Payload) ?? throw new InvalidOperationException("Missing security batch.");
        var path = $"internal/v1/security-batches/{batch.AgentId:D}/{batch.BatchId:D}";
        var host = clients.CreateClient("host");
        var connection = configuration.GetConnectionString("Security") ?? throw new InvalidOperationException("ConnectionStrings:Security is required.");
        var handler = new TransactionalWorkHandler(connection, async (_, sql, transaction, token) =>
        {
            using var response = await host.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(AgentBatchCodec.MaximumBytes, token);
            var payload = await response.Content.ReadAsByteArrayAsync(token);
            await ApplyAsync(batch, payload, sql, transaction, token);
            return null;
        });
        var result = await handler.ExecuteAsync(work, cancellationToken);
        using var deleted = await host.DeleteAsync(path, cancellationToken);
        deleted.EnsureSuccessStatusCode();
        return result;
    }

    private static async Task ApplyAsync(HostSecurityBatchReady batch, byte[] payload, SqlConnection sql, SqlTransaction transaction, CancellationToken token)
    {
        await using var time = new SqlCommand("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')", sql, transaction);
        var now = (DateTimeOffset)(await time.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Database clock unavailable."));
        if (batch.Kind == "inventory" && InventoryChunkCodec.TryRead(payload, out var inventory) && inventory!.BatchId == batch.BatchId)
        {
            await transaction.SaveAsync("staging", token);
            var result = await new InventoryStageWriter().StageAsync(sql, transaction, new(batch.AgentId, batch.DeviceId, batch.DeviceGeneration), inventory, now, token);
            if (result == InventoryStageResult.CapacityExceeded) throw new InvalidOperationException("Inventory staging is busy.");
            if (result is InventoryStageResult.Conflict or InventoryStageResult.Expired) await transaction.RollbackAsync("staging", token);
        }
        else if (batch.Kind == "integrity" && IntegrityChunkCodec.TryRead(payload, out var integrity) && integrity!.BatchId == batch.BatchId)
        {
            await transaction.SaveAsync("staging", token);
            var result = await new IntegrityStageWriter().StageAsync(sql, transaction, new(batch.AgentId, batch.DeviceId, batch.DeviceGeneration), integrity, now, token);
            if (result == IntegrityStageResult.CapacityExceeded) throw new InvalidOperationException("Integrity staging is busy.");
            if (result is IntegrityStageResult.Conflict or IntegrityStageResult.Expired) await transaction.RollbackAsync("staging", token);
        }
        else if (batch.Kind == "baseline" && BaselineBatchCodec.TryRead(payload, out var baseline) && baseline!.BatchId == batch.BatchId)
        {
            if (!await new BaselineWriter().WriteAsync(sql, transaction, batch.AgentId, batch.DeviceId, batch.DeviceGeneration, baseline, now, token))
                throw new InvalidOperationException("Baseline batch rejected.");
        }
        else if (batch.Kind == "yara" && YaraResultBatchCodec.TryRead(payload, out var yara) && yara!.BatchId == batch.BatchId)
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(sql,
                options => options.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.UseTransactionAsync(transaction, token);
            var scan = await db.YaraScans.FromSqlInterpolated($"SELECT * FROM [security].[YaraScans] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {batch.BatchId} AND [AgentId] = {batch.AgentId} AND [DeviceId] = {batch.DeviceId}").SingleOrDefaultAsync(token);
            if (scan is null || scan.Outcome is not null && scan.Outcome != (int)YaraScanOutcome.Cancelled)
                throw new InvalidOperationException("YARA result does not match a pending scan.");
            if (scan.Outcome is null)
            {
                scan.Outcome = (int)yara.Result.Outcome;
                scan.Output = yara.Result.Output; scan.Error = yara.Result.Error;
                scan.EngineVersion = yara.Result.EngineVersion;
                scan.OutputTruncated = yara.Result.OutputTruncated; scan.CompletedAt = yara.Result.CompletedAt;
                await db.SaveChangesAsync(token);
            }
        }
        else throw new InvalidOperationException("Security batch type or identity does not match its payload.");
    }
}
