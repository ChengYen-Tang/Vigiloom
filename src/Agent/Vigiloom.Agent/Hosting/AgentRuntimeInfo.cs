using System.Reflection;
using System.Runtime.InteropServices;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting;

internal static class AgentRuntimeInfo
{
    internal static string? Version { get; } = NormalizeVersion(typeof(AgentRuntimeInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static AgentRuntimeReport? Report { get; } = CreateReport();

    private static AgentRuntimeReport? CreateReport()
    {
        var rid = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "linux-x64" :
            OperatingSystem.IsWindows() ? RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "win-x64", Architecture.X86 => "win-x86", _ => null } : null;
        var result = Version is null || rid is null ? null : new AgentRuntimeReport(Version, rid);
        return result?.IsValid() == true ? result : null;
    }

    internal static string? NormalizeVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var version = informationalVersion.Split('+', 2)[0];
        return string.IsNullOrWhiteSpace(version) || version.Length > 256 || version.Any(char.IsControl) ? null : version;
    }
}
