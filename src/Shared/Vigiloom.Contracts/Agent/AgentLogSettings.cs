using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

public enum AgentTextLogMode { SingleLine = 1, IndentedContinuation = 2 }

/// <summary>A source configuration and its optimistic concurrency version.</summary>
public sealed record HostLogSourceView(AgentLogSourceSetting Settings, string Version);
/// <summary>Creates a source; its identity and generation are assigned by the platform.</summary>
public sealed record HostLogSourceCreate([property: JsonRequired] AgentLogSourceKind Kind, [property: JsonRequired] string Name,
    [property: JsonRequired] string Target, [property: JsonRequired] bool Enabled, [property: JsonRequired] AgentTextLogMode? TextMode);
/// <summary>Changes a source without changing its native identity.</summary>
public sealed record HostLogSourceUpdate([property: JsonRequired] string Name, [property: JsonRequired] bool Enabled,
    [property: JsonRequired] AgentTextLogMode? TextMode, [property: JsonRequired] string Version);
/// <summary>Retires a source using its last observed concurrency version.</summary>
public sealed record HostLogSourceRetire([property: JsonRequired] string Version);

/// <summary>A source ID identifies one native source; each changed configuration gets a fresh generation.</summary>
public sealed record AgentLogSourceSetting(
    [property: JsonRequired] Guid SourceId,
    [property: JsonRequired] Guid Generation,
    [property: JsonRequired] AgentLogSourceKind Kind,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Target,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] int ParserVersion,
    [property: JsonRequired] AgentTextLogMode? TextMode)
{
    public bool IsValid() => SourceId.Version == 7 && Generation.Version == 7 && Enum.IsDefined(Kind) &&
        ValidText(Name, 256) && ValidText(Target, 1024) && ParserVersion == 1 &&
        (Kind == AgentLogSourceKind.TextFile ? TextMode.HasValue && Enum.IsDefined(TextMode.Value) : TextMode is null) &&
        (Kind != AgentLogSourceKind.Journal || string.Equals(Target, "local", StringComparison.Ordinal)) &&
        (Kind != AgentLogSourceKind.WindowsEvent || Target.Length <= 512) &&
        (Kind != AgentLogSourceKind.TextFile || IsAbsoluteTarget(Target));

    private static bool ValidText(string? text, int maximum) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl);

    private static bool IsAbsoluteTarget(string path) => path.StartsWith('/') ||
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/' ||
        path.StartsWith("\\\\", StringComparison.Ordinal) && path.Length > 2;
}

/// <summary>Identity-independent, bounded desired log sources for one Agent.</summary>
public sealed record AgentLogSettingsSnapshot(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Revision,
    [property: JsonRequired] ImmutableArray<AgentLogSourceSetting> Sources,
    [property: JsonRequired] int MaxBatchRecords)
{
    public const int CurrentSchema = 1;
    public const int MaximumSources = 32;

    public static AgentLogSettingsSnapshot Create(IEnumerable<AgentLogSourceSetting> sources, int maxBatchRecords = AgentLogBatchCodec.MaximumItems)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var ordered = sources.Take(MaximumSources + 1).OrderBy(x => x?.SourceId).ToArray();
        if (!ValidSources(ordered) || maxBatchRecords is < 1 or > AgentLogBatchCodec.MaximumItems) throw new ArgumentException("Invalid Agent log sources or batch limit.", nameof(sources));
        return new(CurrentSchema, ComputeRevision(ordered, maxBatchRecords), ImmutableArray.CreateRange(ordered), maxBatchRecords);
    }

    public bool IsValid() => SchemaVersion == CurrentSchema && !Sources.IsDefault && ValidSources(Sources) &&
        MaxBatchRecords is >= 1 and <= AgentLogBatchCodec.MaximumItems &&
        string.Equals(Revision, ComputeRevision(Sources.OrderBy(x => x.SourceId), MaxBatchRecords), StringComparison.Ordinal);

    /// <summary>Checks identities present in consecutive snapshots; historic ID reuse requires server-side tombstones.</summary>
    public bool CanFollow(AgentLogSettingsSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!IsValid() || !previous.IsValid()) return false;
        var prior = previous.Sources.ToDictionary(source => source.SourceId);
        foreach (var current in Sources)
        {
            if (!prior.TryGetValue(current.SourceId, out var old)) continue;
            if (old.Kind != current.Kind || !string.Equals(old.Target, current.Target, StringComparison.Ordinal)) return false;
            if (old.Generation == current.Generation && old != current) return false;
        }
        return true;
    }

    private static bool ValidSources(IReadOnlyCollection<AgentLogSourceSetting> sources)
    {
        if (sources.Count > MaximumSources) return false;
        var ids = new HashSet<Guid>();
        foreach (var source in sources)
            if (source is null || !source.IsValid() || !ids.Add(source.SourceId)) return false;
        return true;
    }

    private static string ComputeRevision(IEnumerable<AgentLogSourceSetting> sources, int maxBatchRecords)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new RevisionInput(CurrentSchema, sources, maxBatchRecords));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed record RevisionInput(int SchemaVersion, IEnumerable<AgentLogSourceSetting> Sources, int MaxBatchRecords);
}
