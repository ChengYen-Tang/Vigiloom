using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Provides administrator rule testing and state changes without exposing storage entities.</summary>
public sealed class SigmaRuleService
{
    private readonly SigmaTesting testing;
    private readonly SigmaRuleManagement management;

    internal SigmaRuleService(SigmaTesting testing, SigmaRuleManagement management)
    {
        this.testing = testing;
        this.management = management;
    }

    public static bool Valid(SigmaRuleTestRequest? request) => request is not null &&
        !string.IsNullOrWhiteSpace(request.Path) && request.Path.Length <= 512 &&
        request.Samples is { Count: >= 2 and <= SigmaTesting.MaximumSamples } &&
        request.Samples.All(x => x is not null && x.AgentId.Version == 7 && x.MessageId.Version == 7 && Enum.IsDefined(x.Expected)) &&
        request.Samples.Select(x => (x.AgentId, x.MessageId)).Distinct().Count() == request.Samples.Count &&
        SigmaExceptions.Valid(request.Exceptions);

    public static bool Valid(SigmaRuleStateRequest? request) => request is not null && request.SourceId.Version == 7 &&
        !string.IsNullOrWhiteSpace(request.Path) && request.Path.Length <= 512 && request.ExpectedVersion is { Length: <= 32 } &&
        (!request.Enabled || request.TestId is { Version: 7 });

    public async Task<(SigmaRuleTestStatus Status, SigmaRuleTestResponse? Result)> TestAsync(SigmaRuleTestRequest request, Guid actor, CancellationToken token)
    {
        var samples = request.Samples.Select(x => new SigmaTestSample(x.AgentId, x.MessageId, (SigmaMatch)x.Expected)).ToArray();
        var result = await testing.RunAsync(request.Path, samples, actor, token, request.Exceptions);
        return (result.Status switch
        {
            SigmaTestingStatus.Saved => SigmaRuleTestStatus.Saved,
            SigmaTestingStatus.SourceUnavailable => SigmaRuleTestStatus.SourceUnavailable,
            SigmaTestingStatus.RuleUnsupported => SigmaRuleTestStatus.RuleUnsupported,
            SigmaTestingStatus.EventUnavailable => SigmaRuleTestStatus.EventUnavailable,
            _ => throw new InvalidOperationException("Unknown rule test status.")
        }, result.TestId is { } id && result.Passed is { } passed && result.Observations is { } observations
            ? new(id, passed, observations.Select(x => new SigmaRuleTestObservation(x.AgentId, x.MessageId,
                (SigmaExpectedOutcome)x.Expected, (SigmaExpectedOutcome)x.Actual)).ToArray()) : null);
    }

    public async Task<SigmaRuleChangeStatus> SetAsync(SigmaRuleStateRequest request, Guid actor, CancellationToken token) =>
        (await management.SetAsync(request.SourceId, request.Path, request.TestId, request.Enabled, request.ExpectedVersion, actor, token)) switch
        {
            SigmaRuleChangeResult.Changed => SigmaRuleChangeStatus.Changed,
            SigmaRuleChangeResult.Unchanged => SigmaRuleChangeStatus.Unchanged,
            SigmaRuleChangeResult.Conflict => SigmaRuleChangeStatus.Conflict,
            SigmaRuleChangeResult.TestUnavailable => SigmaRuleChangeStatus.TestUnavailable,
            SigmaRuleChangeResult.SourceUnavailable => SigmaRuleChangeStatus.SourceUnavailable,
            _ => throw new InvalidOperationException("Unknown rule change status.")
        };
}
