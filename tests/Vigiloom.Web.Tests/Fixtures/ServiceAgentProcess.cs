using System.Diagnostics;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Owns the disposable hosted-runner service installation for one integration test.</summary>
internal sealed class ServiceAgentProcess(Process process, Task<string> errors) : IAsyncDisposable
{
    private Guid? replacement;

    internal void TrackReplacement(Guid updateId)
    {
        if (updateId.Version != 7) throw new ArgumentException("A version 7 update identifier is required.", nameof(updateId));
        replacement = updateId;
    }
    internal static Task<ServiceAgentProcess> InstallLinuxAsync(string script, string bundle, string certificate,
        Uri server, Guid deviceId, string enrollment, CancellationToken token)
    {
        var start = StartInfo("sudo");
        foreach (var argument in new[] { "-n", "--preserve-env=GITHUB_ACTIONS,RUNNER_ENVIRONMENT", "bash", script, bundle, certificate })
            start.ArgumentList.Add(argument);
        return InstallAsync(start, server, deviceId, enrollment, token);
    }

    internal static Task<ServiceAgentProcess> InstallWindowsAsync(string script, string bundle, string certificate,
        Uri server, Guid deviceId, string enrollment, CancellationToken token)
    {
        var start = StartInfo("powershell.exe");
        WindowsPowerShell.Configure(start);
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, bundle, certificate })
            start.ArgumentList.Add(argument);
        return InstallAsync(start, server, deviceId, enrollment, token);
    }

    private static ProcessStartInfo StartInfo(string executable) => new(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

    private static async Task<ServiceAgentProcess> InstallAsync(ProcessStartInfo start, Uri server, Guid deviceId,
        string enrollment, CancellationToken token)
    {
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated Agent installer.");
        var errors = process.StandardError.ReadToEndAsync();
        var owner = new ServiceAgentProcess(process, errors);
        try
        {
            // Enrollment data goes through stdin, never the process command line or test output.
            foreach (var line in new[] { server.ToString(), deviceId.ToString("D"), enrollment })
                await process.StandardInput.WriteLineAsync(line.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            if (await process.StandardOutput.ReadLineAsync(token) != "READY")
                throw new InvalidOperationException("The isolated Agent installation did not become ready.");
            return owner;
        }
        catch
        {
            await owner.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (replacement is { } updateId) await StopReplacementAsync(updateId);
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("CLEANUP");
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await process.WaitForExitAsync(deadline.Token);
            }
            var diagnostic = await errors;
            foreach (var line in diagnostic.Split('\n'))
                if (line.StartsWith("SYSTEMD_TEST ", StringComparison.Ordinal) ||
                    line.StartsWith("WINDOWS_TEST ", StringComparison.Ordinal)) Console.WriteLine(line);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The isolated Agent installation or cleanup failed.");
        }
        finally { process.Dispose(); }
    }

    private static async Task StopReplacementAsync(Guid updateId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        if (OperatingSystem.IsWindows())
        {
            var name = "Vigiloom-Updater-" + updateId.ToString("N");
            var command = $$"""
                $ErrorActionPreference = 'Stop'
                $service = Get-CimInstance Win32_Service -Filter "Name='Vigiloom Updater'"
                if ($service -and $service.State -ne 'Stopped') {
                    Stop-Service -Name 'Vigiloom Updater'
                    (Get-Service -Name 'Vigiloom Updater').WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
                }
                $scheduler = New-Object -ComObject 'Schedule.Service'
                $scheduler.Connect()
                $folder = $scheduler.GetFolder('\')
                function Is-NotFound($exception) {
                    while ($exception.InnerException) { $exception = $exception.InnerException }
                    return $exception.HResult -eq -2147024894
                }
                try { $task = $folder.GetTask('{{name}}') }
                catch { if (Is-NotFound $_.Exception) { exit 0 }; throw }
                if ($task.State -eq 4) {
                    $task.Stop(0)
                    $timer = [Diagnostics.Stopwatch]::StartNew()
                    while ($task.State -eq 4 -and $timer.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200 }
                    if ($task.State -eq 4) { throw 'Updater replacement task did not stop.' }
                }
                try { $folder.DeleteTask('{{name}}', 0) }
                catch { if (-not (Is-NotFound $_.Exception)) { throw } }
                """;
            var result = await RunCleanupAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], deadline.Token);
            if (result.ExitCode != 0) throw new InvalidOperationException("Could not stop the test Updater replacement: " + result.Error);
        }
        else if (OperatingSystem.IsLinux())
        {
            var updater = await RunCleanupAsync("sudo", ["-n", "systemctl", "stop", "vigiloom-updater.service"], deadline.Token);
            if (updater.ExitCode != 0 && !await LinuxUnitMissingAsync("vigiloom-updater.service", deadline.Token))
                throw new InvalidOperationException("Could not stop the test Updater: " + updater.Error);
            var unit = "vigiloom-updater-replace-" + updateId.ToString("N") + ".service";
            var helper = await RunCleanupAsync("sudo", ["-n", "systemctl", "stop", unit], deadline.Token);
            if (helper.ExitCode != 0 && !await LinuxUnitMissingAsync(unit, deadline.Token))
                throw new InvalidOperationException("Could not stop the test Updater replacement: " + helper.Error);
        }
    }

    private static async Task<bool> LinuxUnitMissingAsync(string unit, CancellationToken token)
    {
        var query = await RunCleanupAsync("sudo", ["-n", "systemctl", "show", unit, "--property", "LoadState", "--value"], token);
        return query.ExitCode == 0 && query.Output.Trim() == "not-found";
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCleanupAsync(string executable, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (OperatingSystem.IsWindows() && executable == "powershell.exe") WindowsPowerShell.Configure(start);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var command = Process.Start(start) ?? throw new InvalidOperationException("Could not stop the test updater replacement.");
        var output = command.StandardOutput.ReadToEndAsync(token);
        var error = command.StandardError.ReadToEndAsync(token);
        try { await command.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!command.HasExited) command.Kill(entireProcessTree: true);
            await command.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        await Task.WhenAll(output, error);
        return (command.ExitCode, output.Result, error.Result);
    }
}
