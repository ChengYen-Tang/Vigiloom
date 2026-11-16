using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Evaluates committed Agent events and stores results within the Outbox acknowledgement transaction.</summary>
internal sealed class HostLogDispatchHandler(IDbContextFactory<SecurityAnalysisDbContext> contexts, SigmaEnabledRules enabledRules,
    SigmaCatalog catalog, IHostLogReader logs, TimeProvider clock, INotificationWriter? notifications = null) : IOutboxHandler
{
    public string MessageType => HostLogDispatch.MessageType;

    public async Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var dispatch = JsonSerializer.Deserialize<HostLogDispatch>(message.Payload);
        if (dispatch is null || dispatch.AgentId.Version != 7 || dispatch.DeviceId.Version != 7 || dispatch.DeviceGeneration < 0 ||
            dispatch.MessageIds is not { Count: > 0 and <= AgentLogBatchCodec.MaximumItems } ||
            dispatch.MessageIds.Any(x => x.Version != 7) || dispatch.MessageIds.Distinct().Count() != dispatch.MessageIds.Count)
            throw new InvalidDataException("Invalid host log dispatch.");

        var selection = await enabledRules.LoadAsync(cancellationToken);
        if (selection.Rules.Snapshot.Availability != SigmaAvailability.Ready)
        {
            await using var lookup = await contexts.CreateDbContextAsync(cancellationToken);
            if (await lookup.SigmaRuleActivations.AsNoTracking().AnyAsync(x => x.Enabled, cancellationToken))
                throw new InvalidOperationException("The active rule source is unavailable.");
            return;
        }
        var events = await logs.ReadAsync(dispatch.AgentId, dispatch.MessageIds, cancellationToken);
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        if (!await SigmaEnabledRules.StillCurrentAsync(db, catalog, selection, cancellationToken))
            throw new InvalidOperationException("The rule selection changed during evaluation.");

        var eventIds = dispatch.MessageIds.ToArray();
        var testIds = selection.Versions.Select(x => x.TestId).ToArray();
        var existing = (await db.SigmaDetectionResults.AsNoTracking()
            .Where(x => x.AgentId == dispatch.AgentId && eventIds.Contains(x.MessageId) && testIds.Contains(x.TestId))
            .Select(x => new { x.MessageId, x.TestId, x.ActivationVersion }).ToArrayAsync(cancellationToken))
            .Select(x => (x.MessageId, x.TestId, Version: Convert.ToBase64String(x.ActivationVersion))).ToHashSet();
        var existingGaps = (await db.SigmaEventGaps.AsNoTracking()
            .Where(x => x.AgentId == dispatch.AgentId && eventIds.Contains(x.MessageId))
            .Select(x => new { x.MessageId, x.Kind }).ToArrayAsync(cancellationToken))
            .Select(x => (x.MessageId, x.Kind)).ToHashSet();
        var versions = selection.Versions.ToDictionary(x => x.Path, x => (Rule: x, Encoded: Convert.ToBase64String(x.Version)), StringComparer.Ordinal);
        var exceptions = selection.Versions.ToDictionary(x => x.Path, x => x.Exceptions, StringComparer.Ordinal);
        var present = events.Select(x => x.MessageId).ToHashSet();
        var now = clock.GetUtcNow().ToUniversalTime();
        var pending = 0;
        var pendingNotifications = new List<NotificationRequest>();
        foreach (var missing in eventIds.Where(x => !present.Contains(x) && !existingGaps.Contains((x, 1))))
        {
            db.SigmaEventGaps.Add(new SigmaEventGap { AgentId = dispatch.AgentId, DeviceId = dispatch.DeviceId,
                DeviceGeneration = dispatch.DeviceGeneration, MessageId = missing, Kind = 1, RecordedAt = now });
            await FlushIfFullAsync();
        }
        foreach (var log in events)
        {
            if (selection.UnavailablePaths.Count != 0 && existingGaps.Add((log.MessageId, 2)))
            {
                db.SigmaEventGaps.Add(new SigmaEventGap { AgentId = dispatch.AgentId, DeviceId = dispatch.DeviceId,
                    DeviceGeneration = dispatch.DeviceGeneration, MessageId = log.MessageId, Kind = 2, RecordedAt = now });
                await FlushIfFullAsync();
            }
            var evaluation = SigmaEvaluator.Evaluate(selection.Rules, log, cancellationToken, exceptions);
            foreach (var rule in evaluation.Rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rule.Result == SigmaMatch.No) continue;
                var (version, encoded) = versions[rule.Source.Path];
                if (!existing.Add((log.MessageId, version.TestId, encoded))) continue;
                db.SigmaDetectionResults.Add(new SigmaDetectionResult { AgentId = dispatch.AgentId, DeviceId = dispatch.DeviceId,
                    DeviceGeneration = dispatch.DeviceGeneration, MessageId = log.MessageId, ActivationId = version.Id,
                    TestId = version.TestId, ActivationVersion = version.Version, Result = (int)rule.Result, OccurredAt = log.OccurredAt,
                    ReceivedAt = log.ReceivedAt, EvaluatedAt = now });
                if (notifications is not null && rule.Result == SigmaMatch.Yes)
                    pendingNotifications.Add(SigmaNotification.Create(dispatch.DeviceId, log, rule.Source, version));
                await FlushIfFullAsync();
            }
        }
        await FlushAsync();
        foreach (var rule in selection.Rules.Correlations)
            await new CorrelationDispatch(logs, notifications).HandleAsync(db, rule, versions[rule.Source.Path].Rule, dispatch, events, now, cancellationToken);

        async Task FlushIfFullAsync()
        {
            if (++pending < 200) return;
            await FlushAsync();
        }

        async Task FlushAsync()
        {
            await db.SaveChangesAsync(cancellationToken);
            if (notifications is not null && pendingNotifications.Count != 0)
                await notifications.WriteBatchAsync(pendingNotifications, connection, transaction, cancellationToken);
            pendingNotifications.Clear();
            db.ChangeTracker.Clear();
            pending = 0;
        }
    }
}
