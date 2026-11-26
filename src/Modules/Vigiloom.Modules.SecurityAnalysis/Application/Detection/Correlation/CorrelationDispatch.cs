using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Events;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

/// <summary>Persists correlation state and results inside the caller's fenced Outbox transaction.</summary>
internal sealed class CorrelationDispatch(IHostLogReader logs, INotificationWriter? notifications = null, IDetectionTimelineWriter? timeline = null)
{
    internal async Task HandleAsync(SecurityAnalysisDbContext db, SigmaCorrelationEntry rule, SigmaEnabledRuleVersion version,
        Guid? revisionId, HostLogDispatch dispatch, IReadOnlyList<HostLogEntry> incoming, DateTimeOffset now, CancellationToken token)
    {
        var captured = await new CorrelationEventStore(logs).CaptureAsync(db, version, dispatch, incoming, now, rule.Plan.Rule.Window, token);
        IReadOnlyList<CorrelationObservation> observations;
        IReadOnlyDictionary<(Guid Agent, Guid Message), (Guid Device, long Generation)> devices;
        if (captured.CapacityExceeded)
        {
            var byId = incoming.ToDictionary(x => x.MessageId);
            observations = dispatch.MessageIds.Select(id =>
            {
                if (!byId.TryGetValue(id, out var log))
                    log = new(dispatch.AgentId, id, Guid.Empty, AgentLogSourceKind.WindowsEvent, "", 1, null, now, "",
                        null, null, null, null, null, new(AgentLogGapReason.SourceUnavailable, null));
                return new CorrelationObservation(log, SigmaMatch.Unknown, null);
            }).ToArray();
            devices = dispatch.MessageIds.ToDictionary(id => (dispatch.AgentId, id), _ => (dispatch.DeviceId, dispatch.DeviceGeneration));
        }
        else
        {
            var evaluated = CorrelationEvaluation.Evaluate(rule.Plan, captured.Events, CorrelationEventStore.MaximumLateness,
                CorrelationEventStore.MaximumFutureSkew, CorrelationEventStore.MaximumEvents, token, version.Exceptions);
            observations = CorrelationEvaluation.ApplyCoverage(evaluated, rule.Plan.Rule.Window,
                captured.CompleteSince, captured.IncompleteUntil).Observations;
            devices = captured.References.ToDictionary(x => (x.AgentId, x.MessageId), x => (x.DeviceId, x.DeviceGeneration));
        }

        var existing = new Dictionary<(Guid Agent, Guid Message), SigmaDetectionResult>();
        var pendingNotifications = new List<NotificationRequest>();
        var pendingTimeline = new List<EventTimelineEntry>();
        foreach (var group in observations.GroupBy(x => x.Event.AgentId))
        {
            foreach (var chunk in group.Select(x => x.Event.MessageId).Chunk(AgentLogBatchCodec.MaximumItems))
            {
                var rows = await db.SigmaDetectionResults.Where(x => x.AgentId == group.Key && chunk.Contains(x.MessageId) &&
                    x.TestId == version.TestId && x.ActivationVersion == version.Version).ToArrayAsync(token);
                foreach (var row in rows) existing.Add((row.AgentId, row.MessageId), row);
            }
        }
        foreach (var observation in observations)
        {
            token.ThrowIfCancellationRequested();
            if (observation.Result == SigmaMatch.No) continue;
            var log = observation.Event;
            var key = (log.AgentId, log.MessageId);
            if (existing.TryGetValue(key, out var previous))
            {
                if (previous.Result == (int)SigmaMatch.Unknown && observation.Result == SigmaMatch.Yes)
                {
                    previous.Result = (int)SigmaMatch.Yes;
                    previous.EvaluatedAt = now;
                    previous.CorrelationCount = observation.Match!.Count;
                    previous.CorrelationFirstAt = observation.Match.FirstEventAt;
                    previous.CorrelationLastAt = observation.Match.LastEventAt;
                    if (timeline is not null)
                        await AddTimelineAsync(log, previous.DeviceId, SigmaMatch.Yes, true);
                    if (notifications is not null)
                        pendingNotifications.Add(SigmaNotification.Create(previous.DeviceId, log, rule.Source, version));
                }
                continue;
            }
            var device = devices[key];
            db.SigmaDetectionResults.Add(new SigmaDetectionResult { AgentId = log.AgentId, MessageId = log.MessageId,
                DeviceId = device.Device, DeviceGeneration = device.Generation, ActivationId = version.Id, TestId = version.TestId,
                ActivationVersion = version.Version, Result = (int)observation.Result, OccurredAt = log.OccurredAt,
                ReceivedAt = log.ReceivedAt, EvaluatedAt = now, CorrelationCount = observation.Match?.Count,
                CorrelationFirstAt = observation.Match?.FirstEventAt, CorrelationLastAt = observation.Match?.LastEventAt });
            if (timeline is not null) await AddTimelineAsync(log, device.Device, observation.Result, false);
            if (notifications is not null && observation.Result == SigmaMatch.Yes)
                pendingNotifications.Add(SigmaNotification.Create(device.Device, log, rule.Source, version));
        }
        await db.SaveChangesAsync(token);
        if (timeline is not null && pendingTimeline.Count != 0) await timeline.WriteAsync(db, pendingTimeline, token);
        if (notifications is not null && pendingNotifications.Count != 0)
            foreach (var batch in pendingNotifications.Chunk(200))
                await notifications.WriteBatchAsync(batch, db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), token);
        db.ChangeTracker.Clear();

        async Task AddTimelineAsync(HostLogEntry log, Guid deviceId, SigmaMatch result, bool updated)
        {
            var title = updated ? "行為規則後續命中" : result == SigmaMatch.Yes ? "行為規則命中" : "行為規則未評估";
            var evidence = $"{(updated ? "先前未評估；" : "")}版本 {revisionId:D}；事件 {log.AgentId:D}/{log.MessageId:D}";
            pendingTimeline.Add(new("sigma", updated ? now : log.ReceivedAt, deviceId, "device", $"{title}：{rule.Source.Path}", evidence,
                $"/devices/{deviceId:D}/host-logs/{log.AgentId:D}/{log.MessageId:D}", $"/events/rules/{version.TestId:D}"));
            if (pendingTimeline.Count < 128) return;
            await timeline!.WriteAsync(db, pendingTimeline, token);
            pendingTimeline.Clear();
        }
    }
}
