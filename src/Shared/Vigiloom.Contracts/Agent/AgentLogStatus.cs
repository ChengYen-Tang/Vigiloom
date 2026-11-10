using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>A source observation is a point-in-time result, not proof of complete collection.</summary>
public enum AgentLogSourceState { End = 1, Collected = 2, Full = 3, Unsupported = 4, Unavailable = 5, Conflict = 6 }

public sealed record AgentLogSourceObservation([property: JsonRequired] Guid SourceId,
    [property: JsonRequired] Guid Generation, [property: JsonRequired] AgentLogSourceState State,
    [property: JsonRequired] DateTimeOffset ObservedAt, [property: JsonRequired] int AgeSeconds)
{
    public bool IsValid() => SourceId.Version == 7 && Generation.Version == 7 && Enum.IsDefined(State) &&
        ObservedAt != default && ObservedAt.Offset == TimeSpan.Zero && AgeSeconds is >= 0 and <= 2_592_000;
}

/// <summary>Latest observed results for a particular desired snapshot; omitted sources are unknown.</summary>
public sealed record AgentLogStatusReport([property: JsonRequired] string Revision,
    [property: JsonRequired] bool Throttled, [property: JsonRequired] IReadOnlyList<AgentLogSourceObservation> Sources)
{
    public bool IsValid()
    {
        if (Revision is not { Length: 64 } || !Revision.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            Sources is null || Sources.Count > AgentLogSettingsSnapshot.MaximumSources) return false;
        var ids = new HashSet<Guid>();
        foreach (var source in Sources)
            if (source is null || !source.IsValid() || !ids.Add(source.SourceId)) return false;
        return true;
    }
}
