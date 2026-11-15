using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Checks rule applicability using observed source identity, not an administrator's display name.</summary>
internal static class SigmaLogSource
{
    internal static bool IsSupported(SigmaRuleMetadata metadata) => metadata.Product switch
    {
        "windows" => metadata.Category is null && metadata.Service is (null or "security" or "system" or "application" or "sysmon") ||
            metadata.Category == "process_creation" && metadata.Service is (null or "security" or "sysmon"),
        "linux" => metadata.Category is null && metadata.Service is (null or "sshd"),
        _ => false
    };

    internal static SigmaMatch Evaluate(SigmaRuleMetadata metadata, HostEvent log)
    {
        if (log.Status != HostEventStatus.Ready) return SigmaMatch.Unknown;
        if (!IsSupported(metadata)) return SigmaMatch.Unknown;
        if (metadata.Product == "windows")
        {
            if (log.SourceKind == AgentLogSourceKind.Journal) return SigmaMatch.No;
            if (log.SourceKind != AgentLogSourceKind.WindowsEvent) return SigmaMatch.Unknown;
            if (metadata.Category == "process_creation")
            {
                var security = MatchField("Channel", "Security");
                var sysmon = MatchField("Channel", "Microsoft-Windows-Sysmon/Operational");
                if (security == SigmaMatch.Unknown || sysmon == SigmaMatch.Unknown) return SigmaMatch.Unknown;
                if (metadata.Service == "security" && security != SigmaMatch.Yes || metadata.Service == "sysmon" && sysmon != SigmaMatch.Yes) return SigmaMatch.No;
                if (security == SigmaMatch.Yes) return Combine(MatchField("EventID", "4688"), MatchField("Provider_Name", "Microsoft-Windows-Security-Auditing"));
                if (sysmon == SigmaMatch.Yes) return Combine(MatchField("EventID", "1"), MatchField("Provider_Name", "Microsoft-Windows-Sysmon"));
                return SigmaMatch.No;
            }
            if (metadata.Service is null) return SigmaMatch.Yes;
            var channel = metadata.Service switch
            {
                "security" => "Security",
                "system" => "System",
                "application" => "Application",
                "sysmon" => "Microsoft-Windows-Sysmon/Operational",
                _ => null
            };
            return channel is null ? SigmaMatch.Unknown : MatchField("Channel", channel);
        }
        if (metadata.Product == "linux")
        {
            if (log.SourceKind == AgentLogSourceKind.WindowsEvent) return SigmaMatch.No;
            if (log.SourceKind != AgentLogSourceKind.Journal) return SigmaMatch.Unknown;
            if (metadata.Service is null) return SigmaMatch.Yes;
            return metadata.Service == "sshd" ? MatchField("SYSLOG_IDENTIFIER", "sshd") : SigmaMatch.Unknown;
        }
        return SigmaMatch.Unknown;

        SigmaMatch MatchField(string field, string expected)
        {
            if (!log.Fields.TryGetValue(field, out var values)) return SigmaMatch.Unknown;
            return values.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) ? SigmaMatch.Yes : SigmaMatch.No;
        }
    }

    private static SigmaMatch Combine(SigmaMatch left, SigmaMatch right) =>
        left == SigmaMatch.No || right == SigmaMatch.No ? SigmaMatch.No :
        left == SigmaMatch.Unknown || right == SigmaMatch.Unknown ? SigmaMatch.Unknown : SigmaMatch.Yes;
}
