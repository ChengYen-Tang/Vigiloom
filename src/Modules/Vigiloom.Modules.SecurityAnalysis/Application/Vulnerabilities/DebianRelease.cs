namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Maps Debian's reported major version to its security tracker release name.</summary>
internal static class DebianRelease
{
    internal static string? TrackerRelease(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var major = version.Split('.', 2)[0];
        return major switch
        {
            "10" => "buster",
            "11" => "bullseye",
            "12" => "bookworm",
            "13" => "trixie",
            _ => null
        };
    }
}
