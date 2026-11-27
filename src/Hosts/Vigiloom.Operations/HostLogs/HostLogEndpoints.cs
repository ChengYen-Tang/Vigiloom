using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Operations.HostLogs;

internal static class HostLogEndpoints
{
    internal static void MapHostLogQuery(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/host-logs", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until,
            string? cursor, Guid? sourceId, HttpContext context, HostLogHistory history, IHttpClientFactory clients,
            IDbContextFactory<OperationsDbContext> contexts, IOperationAuditWriter<OperationAudit> audit, TimeProvider clock, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty) return Results.Forbid();
            try
            {
                if (deviceId == Guid.Empty || until <= from || until - from > TimeSpan.FromDays(31) || sourceId is { Version: not 7 })
                    throw new ArgumentException("Invalid host log query.");
                using var device = await clients.CreateClient("Resources").GetAsync($"internal/v1/devices/{deviceId:D}/agent", token);
                if (device.StatusCode == HttpStatusCode.NotFound) return Results.Problem(statusCode: 404, title: "Device not found");
                device.EnsureSuccessStatusCode();
                var page = await history.GetAsync(deviceId, from, until, cursor, sourceId, token);
                var retention = await clients.CreateClient("Observability").GetFromJsonAsync<TelemetryRetentionSnapshot>("internal/v1/telemetry-retention", token)
                    ?? throw new InvalidOperationException("Observation service returned an empty retention policy.");
                await using var db = await contexts.CreateDbContextAsync(token);
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
                    new OperationAudit(actor, deviceId, clock.GetUtcNow().ToUniversalTime(), new EventEvidenceAccessed("host-log-list", deviceId, deviceId)), token);
                await transaction.CommitAsync(token);
                return Results.Ok(page with { RetentionDays = retention.LogDays });
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid host log query"); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<HostLogPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
    }
}
