using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Hosting.Integrity;

/// <summary>Round-robins enabled scopes and measures intervals using monotonic time.</summary>
internal sealed class IntegritySchedule(TimeProvider clock)
{
    private readonly Dictionary<Guid, (long Revision, long Attempt, TimeSpan Delay)> attempts = [];
    private Guid? last;

    public IntegrityScope? Next(IReadOnlyList<IntegrityScope> scopes)
    {
        var enabled = scopes.Where(scope => scope.Enabled).ToArray();
        var current = enabled.Select(scope => scope.Id).ToHashSet();
        foreach (var removed in attempts.Keys.Where(id => !current.Contains(id)).ToArray()) attempts.Remove(removed);
        if (enabled.Length == 0) { last = null; return null; }

        var previous = last is { } id ? Array.FindIndex(enabled, scope => scope.Id == id) : -1;
        for (var offset = 1; offset <= enabled.Length; offset++)
        {
            var scope = enabled[(previous + offset) % enabled.Length];
            if (!attempts.TryGetValue(scope.Id, out var attempt) || attempt.Revision != scope.Revision ||
                clock.GetElapsedTime(attempt.Attempt) >= attempt.Delay) return scope;
        }
        return null;
    }

    public void Finish(IntegrityScope scope)
    {
        Record(scope, TimeSpan.FromSeconds(scope.ScanIntervalSeconds));
    }

    public void Defer(IntegrityScope scope, TimeSpan delay) => Record(scope, delay);

    public void Trigger(Guid scopeId) => attempts.Remove(scopeId);

    private void Record(IntegrityScope scope, TimeSpan delay)
    {
        attempts[scope.Id] = (scope.Revision, clock.GetTimestamp(), delay);
        last = scope.Id;
    }

    public void Reset() { attempts.Clear(); last = null; }
}
