using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Matches received host events against the selected IOC source.</summary>
internal sealed class IocHostLogHandler(IIocSource source, IHostLogReader logs, TimeProvider clock,
    ILogger<IocHostLogHandler> logger, INotificationWriter? notifications = null) : IOutboxHandler
{
    public string MessageType => HostLogDispatch.IocMessageType;

    public async Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var dispatch = JsonSerializer.Deserialize<HostLogDispatch>(message.Payload);
        if (dispatch is null || dispatch.AgentId.Version != 7 || dispatch.DeviceId.Version != 7 ||
            dispatch.MessageIds is not { Count: > 0 and <= AgentLogBatchCodec.MaximumItems } ||
            dispatch.MessageIds.Any(x => x.Version != 7)) throw new InvalidDataException("Invalid host log dispatch.");

        var selection = await source.GetActiveAsync(cancellationToken);
        if (selection.Availability == IocSourceAvailability.Disabled) return;
        if (selection.Availability != IocSourceAvailability.Ready || selection.SourceId is not { } sourceId ||
            selection.RevisionId is not { } revisionId || selection.ActivationVersion is null)
        {
            if (!await IsEnabledAsync()) return;
            logger.LogWarning("IOC source is unavailable: {Status}.", selection.Availability);
            throw new InvalidOperationException("The active IOC source is unavailable.");
        }
        var hashes = await source.ReadFileAsync(selection, IocSourceEligibility.HashPath, cancellationToken);
        var addresses = await source.ReadFileAsync(selection, IocSourceEligibility.C2Path, cancellationToken);
        if (hashes.Availability != IocSourceAvailability.Ready || addresses.Availability != IocSourceAvailability.Ready ||
            hashes.Content is null || addresses.Content is null)
        {
            if (!await IsEnabledAsync()) return;
            logger.LogWarning("IOC source changed or became unavailable while reading its files.");
            throw new InvalidOperationException("The IOC source changed or became unavailable.");
        }
        var events = await logs.ReadAsync(dispatch.AgentId, dispatch.MessageIds, cancellationToken);
        var matcher = new IocMatcher(hashes.Content, addresses.Content);
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var current = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, cancellationToken);
        if (current is null || !current.Enabled || !current.IocDetectionEnabled) return;
        if (active is null || active.RevisionId != revisionId || Convert.ToBase64String(active.Version) != selection.ActivationVersion)
            throw new InvalidOperationException("The active IOC source changed during evaluation.");
        var now = clock.GetUtcNow();
        var notices = new List<NotificationRequest>();
        foreach (var log in events)
        {
            foreach (var match in matcher.Match(HostEventDecoder.Decode(log)).Distinct())
            {
                db.IocDetections.Add(new IocDetection
                {
                    AgentId = dispatch.AgentId, DeviceId = dispatch.DeviceId, MessageId = log.MessageId,
                    SourceId = sourceId, RevisionId = revisionId, Kind = match.Kind, Value = match.Value,
                    Field = match.Field, SourceLine = match.SourceLine, OccurredAt = log.OccurredAt,
                    ReceivedAt = log.ReceivedAt, EvaluatedAt = now
                });
                if (notifications is not null)
                    notices.Add(new(Guid.CreateVersion7(), "ioc", log.OccurredAt ?? log.ReceivedAt, "IOC 比對線索",
                        $"{match.Kind}：{match.Value}；欄位：{match.Field}；情報版本：{revisionId:D}；原始事件：{log.AgentId:D}/{log.MessageId:D}。此結果需要進一步調查。",
                        ResourceId: dispatch.DeviceId, EvidenceOwnerId: log.AgentId, EvidenceId: log.MessageId));
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (notifications is not null && notices.Count > 0)
            foreach (var batch in notices.Chunk(200)) await notifications.WriteBatchAsync(batch, connection, transaction, cancellationToken);

        async Task<bool> IsEnabledAsync()
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT TOP (1) 1 FROM [security].[IntelligenceSources] WHERE [Key] = 'signature-base' AND [Enabled] = 1 AND [IocDetectionEnabled] = 1";
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
    }
}
