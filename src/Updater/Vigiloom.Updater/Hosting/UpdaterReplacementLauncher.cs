using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Hosting;

/// <summary>Starts the signed updater replacement script outside the running service's process group.</summary>
internal static class UpdaterReplacementLauncher
{
    public static async Task LaunchAsync(UpdateState state, string agentRoot, string stateDirectory, CancellationToken token)
    {
        if (state.Id.Version != 7 || state.PendingUpdaterDirectory is null || state.Phase != UpdatePhase.Succeeded)
            throw new ArgumentException("A completed update with a bundled Updater is required.", nameof(state));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var target = Path.GetFullPath(state.TargetDirectory);
        var staging = Path.GetFullPath(Path.Combine(agentRoot, "staging"));
        var source = Path.GetFullPath(state.PendingUpdaterDirectory);
        if (!string.Equals(Path.GetDirectoryName(target), staging, comparison) ||
            !Guid.TryParseExact(Path.GetFileName(target), "N", out var targetId) || targetId.Version != 7 ||
            !string.Equals(source, Path.Combine(target, "updater"), comparison))
            throw new InvalidDataException("The bundled Updater is outside the verified staging directory.");

        var operation = state.Id.ToString("N");
        var windows = OperatingSystem.IsWindows();
        var suffix = windows ? ".ps1" : ".sh";
        var script = Path.Combine(stateDirectory, "replace-updater-" + operation + suffix);
        var original = Path.Combine(AppContext.BaseDirectory, "Maintenance", windows ? "replace-windows.ps1" : "replace-linux.sh");
        File.Copy(original, script, overwrite: true);
        if (windows)
            await LaunchWindowsAsync(script, agentRoot, source, stateDirectory, operation, token);
        else if (OperatingSystem.IsLinux())
            await RunAsync("systemd-run", ["--unit=vigiloom-updater-replace-" + operation, "--collect", "--property=Type=exec",
                "/bin/bash", script, agentRoot, source, stateDirectory, operation], token);
        else throw new PlatformNotSupportedException();
    }

    private static async Task LaunchWindowsAsync(string script, string agentRoot, string source, string stateDirectory,
        string operation, CancellationToken token)
    {
        static string Quote(string value) => '"' + value + '"';
        var command = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(script) +
            " -AgentRoot " + Quote(agentRoot) + " -Source " + Quote(source) + " -State " + Quote(stateDirectory) + " -Operation " + operation;
        var boundary = DateTime.Now.AddMinutes(2).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><TimeTrigger><StartBoundary>{boundary}</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
              <Principals><Principal id="System"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><ExecutionTimeLimit>PT10M</ExecutionTimeLimit><Enabled>true</Enabled></Settings>
              <Actions Context="System"><Exec><Command>powershell.exe</Command><Arguments>{SecurityElement.Escape(command)}</Arguments></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(stateDirectory, "replace-updater-" + operation + ".xml");
        await File.WriteAllTextAsync(file, xml, Encoding.Unicode, token);
        var name = "Vigiloom-Updater-" + operation;
        await RunAsync("schtasks.exe", ["/Create", "/TN", name, "/XML", file, "/RU", "SYSTEM", "/F"], token);
        await RunAsync("schtasks.exe", ["/Run", "/TN", name], token);
    }

    private static async Task RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not launch the updater replacement.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw new IOException($"Updater replacement launch failed with exit code {process.ExitCode}.");
    }
}
