using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal sealed record SigmaTestSample(Guid AgentId, Guid MessageId, SigmaMatch Expected);
internal enum SigmaTestingStatus { Saved, SourceUnavailable, RuleUnsupported, EventUnavailable }
internal sealed record SigmaTestingResult(SigmaTestingStatus Status, Guid? TestId = null, bool? Passed = null, string? Detail = null,
    IReadOnlyList<SigmaReplayObservation>? Observations = null);

/// <summary>Replays retained events and saves evidence only while its source selection remains current.</summary>
internal sealed class SigmaTesting(IDbContextFactory<SecurityAnalysisDbContext> contexts, SigmaCatalog catalog,
    IHostLogReader logs, TimeProvider clock)
{
    internal const string MatcherVersion = "1";
    internal const int MaximumSamples = 20;

    internal async Task<SigmaTestingResult> RunAsync(string path, IReadOnlyList<SigmaTestSample> samples, Guid actorId, CancellationToken token,
        IReadOnlyList<SigmaRuleException>? exceptions = null)
    {
        if (actorId.Version != 7 || samples.Count is < 2 or > MaximumSamples ||
            samples.Any(x => x.AgentId.Version != 7 || x.MessageId.Version != 7 || !Enum.IsDefined(x.Expected)) ||
            samples.Select(x => (x.AgentId, x.MessageId)).Distinct().Count() != samples.Count || !SigmaExceptions.Valid(exceptions))
            throw new ArgumentException("Invalid replay samples or administrator.");

        var snapshot = await catalog.GetActiveAsync(token);
        if (snapshot.Availability != SigmaAvailability.Ready) return new(SigmaTestingStatus.SourceUnavailable);
        var content = await catalog.ReadRuleAsync(snapshot, path, token);
        if (content.Availability != SigmaAvailability.Ready || content.Rule is null || content.Content is null)
            return new(SigmaTestingStatus.SourceUnavailable);
        var metadata = SigmaRuleValidator.Validate(content.Content);
        SigmaRuleSet rules;
        if (metadata.IsCorrelation)
        {
            rules = await new SigmaRuleSetLoader(catalog).LoadAsync(token);
            if (rules.Snapshot != snapshot) return new(SigmaTestingStatus.SourceUnavailable);
            if (!rules.Correlations.Any(x => x.Source.Path == path && x.Source.Sha256 == content.Rule.Sha256))
                return new(SigmaTestingStatus.RuleUnsupported, Detail: rules.Rejected.FirstOrDefault(x => x.Source.Path == path)?.Reason);
        }
        else
        {
            var compiled = SigmaRuleCompiler.Compile(content.Content, metadata);
            if (compiled.Rule is null) return new(SigmaTestingStatus.RuleUnsupported, Detail: compiled.Error);
            rules = new(snapshot, [new(content.Rule, compiled.Rule)], []);
        }

        var entries = new Dictionary<(Guid Agent, Guid Message), HostLogEntry>();
        foreach (var group in samples.GroupBy(x => x.AgentId))
        {
            var found = await logs.ReadAsync(group.Key, group.Select(x => x.MessageId).ToArray(), token);
            foreach (var entry in found) entries.Add((entry.AgentId, entry.MessageId), entry);
        }
        if (entries.Count != samples.Count) return new(SigmaTestingStatus.EventUnavailable);
        var replaySamples = samples.Select(x => new SigmaReplaySample(entries[(x.AgentId, x.MessageId)], x.Expected)).ToArray();
        var replay = metadata.IsCorrelation
            ? SigmaReplay.RunCorrelation(rules, path, replaySamples, CorrelationEventStore.MaximumLateness,
                CorrelationEventStore.MaximumFutureSkew, MaximumSamples, token, exceptions)
            : SigmaReplay.Run(rules, path, replaySamples, exceptions);
        var observations = replay.Observations.Select(x => new
        {
            x.AgentId, x.MessageId, x.Expected, x.Actual,
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entries[(x.AgentId, x.MessageId)].Content)))
        }).ToArray();
        object evidence = new { Exceptions = exceptions ?? [], Samples = observations };
        if (metadata.IsCorrelation)
        {
            var baseRule = rules.Correlations.Single(x => x.Source.Path == path).Plan.BaseRule.Source;
            evidence = new { BasePath = baseRule.Path, BaseSha256 = baseRule.Sha256,
                MaximumLatenessSeconds = (int)CorrelationEventStore.MaximumLateness.TotalSeconds,
                MaximumFutureSkewSeconds = (int)CorrelationEventStore.MaximumFutureSkew.TotalSeconds, Exceptions = exceptions ?? [], Samples = observations };
        }

        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {snapshot.SourceId}")
            .AsNoTracking().SingleOrDefaultAsync(token);
        if (source is null || await catalog.CheckSelectionAsync(db, snapshot, token) != SigmaAvailability.Ready)
            return new(SigmaTestingStatus.SourceUnavailable);
        var record = new SigmaRuleTest
        {
            Id = Guid.CreateVersion7(), SourceId = snapshot.SourceId!.Value, RevisionId = snapshot.RevisionId!.Value,
            Path = content.Rule.Path, Digest = content.Rule.Sha256, ActivationVersion = snapshot.ActivationVersion!,
            MatcherVersion = MatcherVersion, SamplesJson = JsonSerializer.Serialize(evidence), Passed = replay.Passed,
            ActorId = actorId, TestedAt = clock.GetUtcNow().ToUniversalTime()
        };
        db.SigmaRuleTests.Add(record);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(SigmaTestingStatus.Saved, record.Id, record.Passed, Observations: replay.Observations);
    }
}
