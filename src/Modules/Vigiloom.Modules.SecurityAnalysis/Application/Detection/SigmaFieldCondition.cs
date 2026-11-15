namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal enum SigmaMatch { No, Yes, Unknown }

/// <summary>Matches one Sigma field without treating unavailable event data as a negative result.</summary>
internal sealed class SigmaFieldCondition
{
    private readonly string? field;
    private readonly SigmaPattern[] patterns;
    private readonly bool all;
    private readonly bool? exists;

    internal SigmaFieldCondition(string field, SigmaPattern[] patterns, bool all = false, bool? exists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(patterns);
        if (exists is null && patterns.Length == 0 || exists is not null && patterns.Length != 0)
            throw new ArgumentException("A field needs either values or an existence test.", nameof(patterns));
        this.field = field;
        this.patterns = patterns;
        this.all = all;
        this.exists = exists;
    }

    private SigmaFieldCondition(SigmaPattern[] patterns, bool all)
    {
        if (patterns.Length == 0) throw new ArgumentException("Keyword selection is empty.", nameof(patterns));
        this.patterns = patterns;
        this.all = all;
    }

    internal static SigmaFieldCondition Keywords(SigmaPattern[] patterns, bool all = false) => new(patterns, all);

    internal SigmaMatch Evaluate(HostEvent log)
    {
        if (log.Status != HostEventStatus.Ready) return SigmaMatch.Unknown;
        if (field is null) return EvaluateKeywords(log);
        var found = log.Fields.TryGetValue(field, out var values);
        if (exists is { } required)
        {
            if (!found && log.HasOpaqueFields) return SigmaMatch.Unknown;
            return found == required ? SigmaMatch.Yes : SigmaMatch.No;
        }
        if (!found) return SigmaMatch.Unknown;
        foreach (var pattern in patterns)
        {
            var matched = false;
            foreach (var value in values!)
                if (pattern.Matches(value)) { matched = true; break; }
            if (matched != all) return matched ? SigmaMatch.Yes : SigmaMatch.No;
        }
        return all ? SigmaMatch.Yes : SigmaMatch.No;
    }

    private SigmaMatch EvaluateKeywords(HostEvent log)
    {
        if (log.SourceKind == Vigiloom.Contracts.Agent.AgentLogSourceKind.Journal && !log.Fields.ContainsKey("MESSAGE"))
            return SigmaMatch.Unknown;
        if (log.SourceKind != Vigiloom.Contracts.Agent.AgentLogSourceKind.Journal && log.RawText is null)
            return SigmaMatch.Unknown;
        foreach (var pattern in patterns)
        {
            var matched = false;
            if (log.SourceKind != Vigiloom.Contracts.Agent.AgentLogSourceKind.Journal)
                matched = pattern.Matches(log.RawText!);
            else
                matched = MatchValues(log.Fields["MESSAGE"]);
            if (matched != all) return matched ? SigmaMatch.Yes : SigmaMatch.No;

            bool MatchValues(List<string> values)
            {
                foreach (var value in values) if (pattern.Matches(value)) return true;
                return false;
            }
        }
        return all ? SigmaMatch.Yes : SigmaMatch.No;
    }
}
