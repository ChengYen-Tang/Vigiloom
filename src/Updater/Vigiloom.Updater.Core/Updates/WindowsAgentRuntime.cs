using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Vigiloom.Updater.Core.Updates;

/// <summary>Controls the Agent service without changing the Updater service.</summary>
/// <remarks>The installer must protect the installation root and service configuration.
/// The caller must verify the selected package and current assignment before switching.</remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsAgentRuntime(string installationRoot) : IAgentUpdateRuntime
{
    private const string ServiceName = "Vigiloom Agent";
    private const uint NoChange = 0xffffffff;
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StableDuration = TimeSpan.FromSeconds(10);
    private readonly string root = Path.GetFullPath(installationRoot);

    public Task<string> GetSelectedDirectoryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var service = new ServiceController(ServiceName);
        using var handle = service.ServiceHandle;
        var (executable, _) = ParseServiceCommand(ReadServiceCommand(handle));
        if (!Path.IsPathFullyQualified(executable)) throw new InvalidOperationException("The Agent service executable path is not absolute.");
        return Task.FromResult(Path.GetDirectoryName(Path.GetFullPath(executable))!);
    }

    public async Task StopAsync(CancellationToken token)
    {
        using var service = new ServiceController(ServiceName);
        var status = await WaitWhileAsync(service, ServiceControllerStatus.StartPending, token);
        if (status == ServiceControllerStatus.Stopped)
        {
            await WaitForInstalledProcessesAsync(token);
            return;
        }
        using var process = status is ServiceControllerStatus.Running or ServiceControllerStatus.Paused
            ? OpenServiceProcess(service) : null;
        if (status != ServiceControllerStatus.StopPending) service.Stop(stopDependentServices: false);
        await WaitForAsync(service, ServiceControllerStatus.Stopped, token);
        if (process is not null)
        {
            await WaitForExitAsync(process, token);
        }
        await WaitForInstalledProcessesAsync(token);
    }

    public async Task StartAsync(CancellationToken token)
    {
        using var service = new ServiceController(ServiceName);
        var status = await WaitWhileAsync(service, ServiceControllerStatus.StopPending, token);
        if (status == ServiceControllerStatus.Running) return;
        if (status != ServiceControllerStatus.StartPending) service.Start();
        await WaitForAsync(service, ServiceControllerStatus.Running, token);
    }

    public Task SelectVersionAsync(string versionDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(versionDirectory);
        var executable = Path.Combine(target, "Vigiloom.Agent.exe");
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable) || executable.Contains('"'))
            throw new InvalidOperationException("The selected Agent version is not present under the installation root.");
        using var service = new ServiceController(ServiceName);
        using var handle = service.ServiceHandle;
        var (_, arguments) = ParseServiceCommand(ReadServiceCommand(handle));
        if (ChangeServiceConfig(handle, NoChange, NoChange, NoChange, $"\"{executable}\"{arguments}", 0, 0, 0, 0, 0, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not switch the Agent service executable.");
        return Task.CompletedTask;
    }

    public async Task<bool> WaitHealthyAsync(CancellationToken token)
    {
        using var service = new ServiceController(ServiceName);
        using var handle = service.ServiceHandle;
        var elapsed = Stopwatch.StartNew();
        var stable = Stopwatch.StartNew();
        uint previousPid = 0;
        while (elapsed.Elapsed < HealthTimeout)
        {
            token.ThrowIfCancellationRequested();
            if (QueryServiceStatusEx(handle, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not query the Agent service.");
            var pid = status.CurrentState == (uint)ServiceControllerStatus.Running ? status.ProcessId : 0;
            if (pid == 0 || pid != previousPid) { previousPid = pid; stable.Restart(); }
            else if (stable.Elapsed >= StableDuration) return true;
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        return false;
    }

    private static async Task<ServiceControllerStatus> WaitWhileAsync(ServiceController service, ServiceControllerStatus pending, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            service.Refresh();
            var status = service.Status;
            if (status != pending) return status;
            if (elapsed.Elapsed >= TransitionTimeout) throw new System.TimeoutException("The Agent service transition timed out.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }

    private static async Task WaitForAsync(ServiceController service, ServiceControllerStatus expected, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            service.Refresh();
            if (service.Status == expected) return;
            if (elapsed.Elapsed >= TransitionTimeout) throw new System.TimeoutException("The Agent service transition timed out.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }

    private static Process? OpenServiceProcess(ServiceController service)
    {
        using var handle = service.ServiceHandle;
        if (QueryServiceStatusEx(handle, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not query the Agent service process.");
        if (status.CurrentState is not ((uint)ServiceControllerStatus.Running) and not ((uint)ServiceControllerStatus.Paused) || status.ProcessId == 0)
            throw new InvalidOperationException("The Agent service process is no longer available.");
        try
        {
            var process = Process.GetProcessById(checked((int)status.ProcessId));
            try { _ = process.Handle; return process; }
            catch { process.Dispose(); throw; }
        }
        catch (ArgumentException) { return null; } // The process already exited.
    }

    private async Task WaitForInstalledProcessesAsync(CancellationToken token)
    {
        // Recovery may start after the SCM has already entered StopPending, when its PID is not reliable.
        // Check the protected installation path as well so that an old process cannot outlive the stop.
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var processes = Process.GetProcessesByName("Vigiloom.Agent");
        try
        {
            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var path = ProcessImagePath(process);
                    if (Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        await WaitForExitAsync(process, token);
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static unsafe string ProcessImagePath(Process process)
    {
        var buffer = new char[32768];
        uint length = (uint)buffer.Length;
        fixed (char* pointer = buffer)
        {
            if (QueryFullProcessImageName(process.SafeHandle, 0, pointer, ref length) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not identify an Agent process.");
        }
        return new string(buffer, 0, checked((int)length));
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TransitionTimeout);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new System.TimeoutException("The Agent process did not exit after the service stopped."); }
    }

    private static string ReadServiceCommand(SafeHandle service)
    {
        if (QueryServiceConfig(service, 0, 0, out var required) != 0 || Marshal.GetLastPInvokeError() != 122 || required == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the Agent service configuration.");
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (QueryServiceConfig(service, buffer, required, out _) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the Agent service configuration.");
            var config = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            var command = Marshal.PtrToStringUni(config.BinaryPath) ?? throw new InvalidOperationException("The Agent service executable is missing.");
            return command;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static (string Executable, string Arguments) ParseServiceCommand(string command)
    {
        var start = command.AsSpan().TrimStart();
        if (start.IsEmpty) throw new InvalidOperationException("The Agent service executable is missing.");
        var quoted = start[0] == '"';
        var end = quoted ? start[1..].IndexOf('"') + 2 : start.IndexOfAny(' ', '\t');
        if (quoted && end == 1) throw new InvalidOperationException("The Agent service command line has invalid quotes.");
        if (quoted && end < start.Length && start[end] is not (' ' or '\t'))
            throw new InvalidOperationException("The Agent service command line has invalid quotes.");
        var executable = quoted ? start[1..(end - 1)] : end < 0 ? start : start[..end];
        if (!Path.GetFileName(executable).Equals("Vigiloom.Agent.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Agent service command line has an unexpected or unquoted executable path.");
        return (executable.ToString(), end < 0 ? "" : start[end..].ToString());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        public uint ServiceType, StartType, ErrorControl;
        public nint BinaryPath, LoadOrderGroup;
        public uint TagId;
        public nint Dependencies, StartName, DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode;
        public uint CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int ChangeServiceConfig(SafeHandle service, uint type, uint start, uint error, string binaryPath,
        nint loadOrderGroup, nint tag, nint dependencies, nint account, nint password, nint displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int QueryServiceStatusEx(SafeHandle service, int level, out ServiceStatusProcess status, uint size, out uint needed);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int QueryServiceConfig(SafeHandle service, nint config, uint size, out uint needed);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int QueryFullProcessImageName(SafeHandle process, uint flags, char* path, ref uint size);
}
