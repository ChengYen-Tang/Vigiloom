using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Intelligence;

/// <summary>Expected result when replaying a retained Agent event.</summary>
public enum SigmaExpectedOutcome { NoMatch = 0, Match = 1, Unknown = 2 }
/// <summary>One retained Agent event used to test a rule.</summary>
public sealed record SigmaRuleTestSample(Guid AgentId, Guid MessageId, [property: JsonRequired] SigmaExpectedOutcome Expected);
/// <summary>A local exact field-value exclusion; upstream rules remain unchanged.</summary>
public sealed record SigmaRuleException(string Field, string Value);
/// <summary>Requests a bounded replay of a rule against retained events.</summary>
public sealed record SigmaRuleTestRequest(string Path, IReadOnlyList<SigmaRuleTestSample> Samples,
    IReadOnlyList<SigmaRuleException>? Exceptions = null);
/// <summary>The expected and observed results for a replayed event.</summary>
public sealed record SigmaRuleTestObservation(Guid AgentId, Guid MessageId, SigmaExpectedOutcome Expected, SigmaExpectedOutcome Actual);
/// <summary>Identifies a saved rule replay and reports the result of each example.</summary>
public sealed record SigmaRuleTestResponse(Guid TestId, bool Passed, IReadOnlyList<SigmaRuleTestObservation> Observations);
/// <summary>Requests enabling a tested rule or disabling its current selection.</summary>
public sealed record SigmaRuleStateRequest(Guid SourceId, string Path, Guid? TestId, [property: JsonRequired] bool Enabled, string ExpectedVersion);
/// <summary>Outcome of a rule replay request.</summary>
public enum SigmaRuleTestStatus { Saved, SourceUnavailable, RuleUnsupported, EventUnavailable }
/// <summary>Outcome of a rule state change.</summary>
public enum SigmaRuleChangeStatus { Changed, Unchanged, Conflict, TestUnavailable, SourceUnavailable }
/// <summary>Source metadata and local state for one Sigma rule; support does not imply host coverage.</summary>
public sealed record SigmaDetectionRuleItem(SigmaSourceRule Source, bool Supported, string? UnsupportedReason,
    Guid? LatestTestId, bool? LatestTestPassed, bool Enabled, bool EnabledForCurrentSource, string Version,
    IReadOnlyList<SigmaRuleException>? LatestExceptions = null, Guid? ActiveTestId = null,
    IReadOnlyList<SigmaRuleException>? ActiveExceptions = null);
/// <summary>A bounded page of rules from the currently selected source revision.</summary>
public sealed record SigmaDetectionRulePage(SigmaSnapshot Source, IReadOnlyList<SigmaDetectionRuleItem> Items, string? NextCursor);
/// <summary>A saved local rule setting, available even when its source is unavailable.</summary>
public sealed record SigmaRuleStateItem(Guid SourceId, string Path, Guid TestId, bool Enabled, string Version);
/// <summary>A bounded page of local rule settings.</summary>
public sealed record SigmaRuleStatePage(IReadOnlyList<SigmaRuleStateItem> Items, int Page, bool HasMore);
/// <summary>The observed event count and time range for a correlation match.</summary>
public sealed record SigmaCorrelationEvidence(int Count, DateTimeOffset FirstEventAt, DateTimeOffset LastEventAt);
/// <summary>A rule match or indeterminate result with the original Agent event reference.</summary>
public sealed record SigmaDetectionEvent(Guid AgentId, Guid MessageId, Guid SourceId, Guid RevisionId, Guid TestId, string MatcherVersion, string ActivationVersion,
    string RulePath, string RuleSha256, SigmaExpectedOutcome Outcome, DateTimeOffset? OccurredAt, DateTimeOffset ReceivedAt, DateTimeOffset EvaluatedAt,
    SigmaCorrelationEvidence? Correlation);
/// <summary>A bounded page of detection results ordered by central receipt time.</summary>
public sealed record SigmaDetectionEventPage(IReadOnlyList<SigmaDetectionEvent> Items, string? NextCursor);
/// <summary>Why a host event could not be fully evaluated.</summary>
public enum SigmaDetectionGapKind { OriginalEventUnavailable = 1, RuleUnavailable = 2 }
/// <summary>A missing or partially evaluated Agent event reference.</summary>
public sealed record SigmaDetectionGap(Guid AgentId, Guid MessageId, SigmaDetectionGapKind Kind, DateTimeOffset RecordedAt);
/// <summary>A bounded page of detection data gaps.</summary>
public sealed record SigmaDetectionGapPage(IReadOnlyList<SigmaDetectionGap> Items, string? NextCursor);
