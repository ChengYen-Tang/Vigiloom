using System.Diagnostics;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Restores Windows PowerShell 5.1 built-in modules when launched from PowerShell 7.</summary>
internal static class WindowsPowerShell
{
    internal static void Configure(ProcessStartInfo start)
    {
        var modules = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules");
        start.Environment.TryGetValue("PSModulePath", out var inherited);
        start.Environment["PSModulePath"] = string.IsNullOrEmpty(inherited) ? modules : modules + Path.PathSeparator + inherited;
    }
}
