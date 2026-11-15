namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Maps a validated Debian tracker release record without confusing archive status with an installed version.
/// The caller must reject malformed input rather than mapping an invalid field to null.
/// A non-empty fixed version on an open record is handled defensively; it is not a typical exporter shape.</summary>
internal static class DebianTrackerStatus
{
    internal static (DebianAdvisoryState State, string? FixedVersion) Normalize(string? status, string? fixedVersion)
    {
        if (status is not ("open" or "resolved" or "undetermined")) return (DebianAdvisoryState.Undetermined, null);
        if (status == "undetermined") return (DebianAdvisoryState.Undetermined, null);
        if (fixedVersion == "0")
            return status == "resolved" ? (DebianAdvisoryState.NotAffected, null) : (DebianAdvisoryState.Undetermined, null);
        if (!string.IsNullOrEmpty(fixedVersion))
            return DebianVersion.TryCompare(fixedVersion, fixedVersion, out _)
                ? (DebianAdvisoryState.FixedVersion, fixedVersion)
                : (DebianAdvisoryState.Undetermined, null);
        return status == "open" ? (DebianAdvisoryState.Open, null) : (DebianAdvisoryState.Undetermined, null);
    }
}
