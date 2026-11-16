using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Line;

/// <summary>Delivers a persisted request through the shared outbox with LINE's retry key.</summary>
public sealed class LineDeliveryHandler(IDbContextFactory<OperationsDbContext> contexts, LinePushClient client,
    IDataProtectionProvider protection, TimeProvider clock) : IOutboxHandler
{
    public const string Type = "line.push.v1";
    public string MessageType => Type;

    public async Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        // Persist the first attempt outside the outbox transaction: LINE may accept the request even if that transaction rolls back.
        await using (var mark = await contexts.CreateDbContextAsync(cancellationToken))
            await mark.LineDeliveries.Where(x => x.Id == message.MessageId && x.FirstAttemptAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.FirstAttemptAt, now), cancellationToken);

        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using var db = new OperationsDbContext(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var delivery = await db.LineDeliveries.SingleOrDefaultAsync(x => x.Id == message.MessageId, cancellationToken);
        if (delivery is null || delivery.CompletedAt is not null) return;
        var settings = await db.LineSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var active = await db.LineGroups.AsNoTracking().AnyAsync(x => x.Id == delivery.GroupId && x.Approved && x.ExternalId == delivery.Recipient, cancellationToken);
        if (settings?.ProtectedAccessToken is null || (delivery.NotificationId is not null && !settings.Enabled) || !active)
            delivery.FailureCode = "disabled";
        else if (delivery.FirstAttemptAt <= now.AddHours(-24))
            delivery.FailureCode = "retry_expired";
        else
        {
            var accessToken = protection.CreateProtector(LineChannel.ProtectionPurpose).Unprotect(settings.ProtectedAccessToken);
            var result = await client.SendAsync(accessToken, delivery.Recipient, delivery.Text, delivery.Id, cancellationToken);
            if (result == LinePushResult.Retry) throw new HttpRequestException("LINE push temporarily unavailable.");
            delivery.FailureCode = result == LinePushResult.Rejected ? "rejected" : null;
        }
        delivery.CompletedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }
}
