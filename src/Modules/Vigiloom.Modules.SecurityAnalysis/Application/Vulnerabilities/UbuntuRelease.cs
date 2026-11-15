namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Maps known Ubuntu releases to Canonical's ordinary OSV ecosystem, without inferring Pro coverage.</summary>
internal static class UbuntuRelease
{
    internal static string? Ecosystem(string? version) => version switch
    {
        "18.04" => "Ubuntu:18.04:LTS",
        "20.04" => "Ubuntu:20.04:LTS",
        "22.04" => "Ubuntu:22.04:LTS",
        "24.04" => "Ubuntu:24.04:LTS",
        "26.04" => "Ubuntu:26.04:LTS",
        "23.10" => "Ubuntu:23.10",
        "24.10" => "Ubuntu:24.10",
        "25.04" => "Ubuntu:25.04",
        "25.10" => "Ubuntu:25.10",
        "26.10" => "Ubuntu:26.10",
        _ => null
    };
}
