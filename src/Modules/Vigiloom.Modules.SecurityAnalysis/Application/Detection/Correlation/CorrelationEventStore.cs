using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

internal sealed record CorrelationEventSet(IReadOnlyList<SigmaCorrelationEvent> References,
    IReadOnlyList<HostLogEntry> Events, bool CapacityExceeded, DateTimeOffset CompleteSince, DateTimeOffset? IncompleteUntil);

/// <summary>Loads bounded cross-batch references and stages new ones in the caller's transaction.</summary>
internal sealed class CorrelationEventStore(IHostLogReader logs, int maximumEvents = CorrelationEventStore.MaximumEvents,
    long maximumContentCharacters = CorrelationEventStore.MaximumContentCharacters)
{
    internal const int MaximumEvents = 5000;
    internal const long MaximumContentCharacters = 16 * 1024 * 1024;
    internal static readonly TimeSpan MaximumLateness = SigmaCorrelationLimits.MaximumLateness;
    internal static readonly TimeSpan MaximumFutureSkew = SigmaCorrelationLimits.MaximumFutureSkew;

    internal async Task<CorrelationEventSet> CaptureAsync(SecurityAnalysisDbContext db, SigmaEnabledRuleVersion version,
        HostLogDispatch dispatch, IReadOnlyList<HostLogEntry> incoming, DateTimeOffset receivedAt, TimeSpan window, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("A transaction is required.");
        if (window <= TimeSpan.Zero || window > SigmaCorrelationLimits.MaximumWindow) throw new ArgumentOutOfRangeException(nameof(window));
        if (maximumEvents is < 1 or > MaximumEvents || maximumContentCharacters is < 1 or > MaximumContentCharacters)
            throw new InvalidOperationException("Invalid correlation capacity.");
        var activationVersion = version.Version;
        var cutoffSpan = window + MaximumLateness + MaximumFutureSkew;
        var now = receivedAt.ToUniversalTime();
        var cutoff = now.UtcTicks < cutoffSpan.Ticks ? DateTimeOffset.MinValue : now - cutoffSpan;
        var coverage = await db.SigmaCorrelationCoverages.SingleOrDefaultAsync(
            x => x.TestId == version.TestId && x.ActivationVersion == activationVersion, token);
        if (coverage is null)
        {
            coverage = new SigmaCorrelationCoverage { TestId = version.TestId, ActivationVersion = activationVersion,
                CompleteSince = now };
            db.SigmaCorrelationCoverages.Add(coverage);
        }
        else if (cutoff + MaximumFutureSkew > coverage.CompleteSince) coverage.CompleteSince = cutoff + MaximumFutureSkew;
        await db.SigmaCorrelationEvents.Where(x => x.TestId == version.TestId && x.ActivationVersion == activationVersion &&
            (x.OccurredAt < cutoff || x.ReceivedAt < cutoff)).ExecuteDeleteAsync(token);
        var references = await db.SigmaCorrelationEvents.AsNoTracking()
            .Where(x => x.TestId == version.TestId && x.ActivationVersion == activationVersion)
            .OrderBy(x => x.RowId).Take(maximumEvents + 1).ToListAsync(token);
        if (references.Count > maximumEvents) return Overflow();
        var ids = references.Select(x => (x.AgentId, x.MessageId)).ToHashSet();
        var incomingById = incoming.ToDictionary(x => x.MessageId);
        var added = new List<SigmaCorrelationEvent>();
        foreach (var id in dispatch.MessageIds)
        {
            token.ThrowIfCancellationRequested();
            if (!ids.Add((dispatch.AgentId, id))) continue;
            if (ids.Count > maximumEvents) return Overflow();
            incomingById.TryGetValue(id, out var log);
            added.Add(new SigmaCorrelationEvent { TestId = version.TestId, ActivationVersion = activationVersion,
                AgentId = dispatch.AgentId, MessageId = id, DeviceId = dispatch.DeviceId, DeviceGeneration = dispatch.DeviceGeneration,
                OccurredAt = log?.OccurredAt, ReceivedAt = log?.ReceivedAt ?? receivedAt });
        }
        references.AddRange(added);
        var loaded = new Dictionary<(Guid Agent, Guid Message), HostLogEntry>(references.Count);
        long characters = 0;
        foreach (var agent in references.GroupBy(x => x.AgentId))
        {
            foreach (var chunk in agent.Select(x => x.MessageId).Chunk(AgentLogBatchCodec.MaximumItems))
            {
                var found = await logs.ReadAsync(agent.Key, chunk, token);
                foreach (var item in found)
                {
                    characters += item.Content.Length;
                    if (characters > maximumContentCharacters) return Overflow();
                    loaded.Add((item.AgentId, item.MessageId), item);
                }
            }
        }
        var events = new List<HostLogEntry>(references.Count);
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            if (loaded.TryGetValue((reference.AgentId, reference.MessageId), out var log)) events.Add(log);
            else events.Add(new(reference.AgentId, reference.MessageId, Guid.Empty, AgentLogSourceKind.WindowsEvent, "", 1,
                reference.OccurredAt, reference.ReceivedAt, "", null, null, null, null, null,
                new(AgentLogGapReason.SourceUnavailable, null)));
        }
        db.SigmaCorrelationEvents.AddRange(added);
        return new(references, events, false, coverage.CompleteSince, coverage.IncompleteUntil);

        CorrelationEventSet Overflow()
        {
            foreach (var id in dispatch.MessageIds)
            {
                var log = incoming.FirstOrDefault(x => x.MessageId == id);
                var latestAllowed = AddClamped(log?.ReceivedAt ?? now, MaximumFutureSkew);
                var basis = log?.OccurredAt is { } occurred && occurred <= latestAllowed ? occurred.ToUniversalTime() : latestAllowed;
                var until = AddClamped(basis, window);
                if (coverage.IncompleteUntil is null || until > coverage.IncompleteUntil) coverage.IncompleteUntil = until;
            }
            return new([], [], true, coverage.CompleteSince, coverage.IncompleteUntil);
        }
    }

    private static DateTimeOffset AddClamped(DateTimeOffset value, TimeSpan amount)
    {
        value = value.ToUniversalTime();
        return value.UtcTicks > DateTimeOffset.MaxValue.UtcTicks - amount.Ticks ? DateTimeOffset.MaxValue : value + amount;
    }
}
