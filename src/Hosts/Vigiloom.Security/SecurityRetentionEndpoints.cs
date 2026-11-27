using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.SecurityAnalysis.Application.Retention;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Security;

internal static class SecurityRetentionEndpoints
{
    public static void MapSecurityRetentionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/retention");
        group.MapPost("/audit/count", async (AuditRetentionRequest request, IDbContextFactory<SecurityAnalysisDbContext> factory, CancellationToken token) =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            return Results.Ok(await new SecurityAuditRetention().CountAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), request.Cutoff, token));
        });
        group.MapPost("/audit/delete", async (AuditRetentionRequest request, IDbContextFactory<SecurityAnalysisDbContext> factory, CancellationToken token) =>
        {
            if (request.BatchSize is < 1 or > 500) return Results.BadRequest();
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var removed = await new SecurityAuditRetention().DeleteBatchAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), request.Cutoff, request.BatchSize, token);
            await tx.CommitAsync(token);
            return Results.Ok(removed);
        });
        group.MapPost("/detections/count", async (SecurityDetectionRetentionRequest request, IDbContextFactory<SecurityAnalysisDbContext> factory, CancellationToken token) =>
        {
            if (!TelemetryRetentionLimits.IsValid(request.Days, request.Days)) return Results.BadRequest();
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            return Results.Ok(await new SecurityDetectionRetention().CountAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), request.Now, request.Days, token));
        });
        group.MapPost("/detections/delete", async (SecurityDetectionRetentionRequest request, IDbContextFactory<SecurityAnalysisDbContext> factory, CancellationToken token) =>
        {
            if (request.BatchSize is < 1 or > 500 || !TelemetryRetentionLimits.IsValid(request.Days, request.Days)) return Results.BadRequest();
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var removed = await new SecurityDetectionRetention().DeleteBatchAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), request.Now, request.Days, request.BatchSize, token);
            await tx.CommitAsync(token);
            return Results.Ok(removed);
        });
    }
}
