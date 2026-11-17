using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigiloom.Updater.Core.Updates;

/// <summary>Controls the installed Agent unit; the Updater itself is a separate unit.</summary>
/// <remarks>
/// The installer owns and protects the installation root and configures the fixed Agent unit
/// with Type=notify and ExecStart pointing to current/Vigiloom.Agent. Only verified versions
/// may be placed beneath that root. The caller must revalidate the assignment before switching.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class SystemdAgentRuntime(string installationRoot) : IAgentUpdateRuntime
{
    private const string Unit = "vigiloom-agent.service";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StableDuration = TimeSpan.FromSeconds(10);
    private readonly string root = Path.GetFullPath(installationRoot);

    public Task<string> GetSelectedDirectoryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var link = new DirectoryInfo(Path.Combine(root, "current"));
        var target = link.ResolveLinkTarget(returnFinalTarget: true);
        if (target is not DirectoryInfo directory || !directory.Exists)
            throw new InvalidOperationException("The current Agent version link is missing or invalid.");
        return Task.FromResult(directory.FullName);
    }

    public Task StopAsync(CancellationToken token) => ExecuteRequiredAsync("stop", token);
    public Task StartAsync(CancellationToken token) => ExecuteRequiredAsync("start", token);

    public Task SelectVersionAsync(string versionDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(versionDirectory);
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(target, "Vigiloom.Agent")))
            throw new InvalidOperationException("The selected Agent version is not present under the installation root.");

        // Creating the link next to its destination keeps rename on one filesystem.
        var link = Path.Combine(root, "current");
        var pending = Path.Combine(root, $".current-{Guid.CreateVersion7():N}");
        // Linux O_DIRECTORY | O_CLOEXEC; opening before rename fails without changing the active link.
        var directory = Open(root, 0x10000 | 0x80000);
        if (directory < 0) throw NativeIoError("Could not open the installation directory.");
        try
        {
            Directory.CreateSymbolicLink(pending, target);
            if (Rename(pending, link) != 0)
                throw NativeIoError("Could not switch the Agent version link.");
            if (Sync(directory) != 0) throw NativeIoError("Could not persist the Agent version link.");
        }
        finally
        {
            try { if (File.Exists(pending) || Directory.Exists(pending)) File.Delete(pending); }
            finally { Close(directory); }
        }
        return Task.CompletedTask;
    }

    [LibraryImport("libc", EntryPoint = "rename", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Rename(string oldPath, string newPath);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Sync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);

    private static IOException NativeIoError(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    public async Task<bool> WaitHealthyAsync(CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        var stable = Stopwatch.StartNew();
        var previousPid = 0;
        while (elapsed.Elapsed < HealthTimeout)
        {
            var result = await ExecuteAsync(["show", Unit, "--property=ActiveState,SubState,MainPID", "--no-pager"], token);
            var state = ParseState(result.Output);
            if (result.ExitCode != 0 || state is null)
            {
                previousPid = 0;
                stable.Restart();
            }
            else if (state.Value != previousPid)
            {
                previousPid = state.Value;
                stable.Restart();
            }
            else if (stable.Elapsed >= StableDuration) return true;
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        return false;
    }

    internal static int? ParseState(string output)
    {
        string? active = null, sub = null, pid = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("ActiveState=", StringComparison.Ordinal)) active = line[12..];
            else if (line.StartsWith("SubState=", StringComparison.Ordinal)) sub = line[9..];
            else if (line.StartsWith("MainPID=", StringComparison.Ordinal)) pid = line[8..];
        }
        return active == "active" && sub == "running" && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : null;
    }

    private static async Task ExecuteRequiredAsync(string action, CancellationToken token)
    {
        var result = await ExecuteAsync([action, Unit, "--no-pager"], token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"systemd could not {action} the Agent service.");
    }

    private static async Task<(int ExitCode, string Output)> ExecuteAsync(string[] arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/bin/systemctl")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(CommandTimeout);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("systemd did not respond in time.");
        }
        finally { try { await error; } catch (OperationCanceledException) { } }
    }
}
