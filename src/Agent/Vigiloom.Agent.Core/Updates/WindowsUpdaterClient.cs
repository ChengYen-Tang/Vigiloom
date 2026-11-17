using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Vigiloom.BuildingBlocks.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Updates;

/// <summary>Hands an update to the Windows Updater service after verifying the pipe server process.</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsUpdaterClient
{
    public const string PipeName = UpdateHandoff.WindowsPipeName;
    private const string ServiceName = "Vigiloom Updater";

    public static Task<UpdateHandoffStatus> SubmitAsync(UpdateHandoff request, CancellationToken token) =>
        SubmitAsync(PipeName, request, token);

    internal static async Task<UpdateHandoffStatus> SubmitAsync(string pipeName, UpdateHandoff request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid()) throw new ArgumentException("A valid update request is required.", nameof(request));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        // GenericWrite also grants FILE_CREATE_PIPE_INSTANCE; request only client operations.
        const PipeAccessRights rights = PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes |
            PipeAccessRights.WriteAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
        await using var pipe = new NamedPipeClientStream(".", pipeName, rights, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(timeout.Token);
        VerifyServer(pipe.SafePipeHandle);
        await BoundedJsonFrame.WriteAsync(pipe, request, timeout.Token);
        var response = await BoundedJsonFrame.ReadAsync<UpdateHandoffResponse>(pipe, timeout.Token);
        if (!response.IsValid()) throw new InvalidDataException("The local Updater response is invalid.");
        return response.Status;
    }

    private static void VerifyServer(SafePipeHandle pipe)
    {
        if (GetNamedPipeServerProcessId(pipe, out var serverPid) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not identify the local Updater.");
        using var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not access the service manager.");
        using var service = OpenService(manager, ServiceName, 4); // SERVICE_QUERY_STATUS
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not find the Updater service.");
        if (QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not query the Updater service.");
        if (status.CurrentState != 4 || serverPid == 0 || status.ProcessId != serverPid)
            throw new UnauthorizedAccessException("The local pipe is not owned by the running Updater service.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode;
        public uint CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle) != 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverPid);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int QueryServiceStatusEx(SafeServiceHandle service, int level, out ServiceStatusProcess status, uint size, out uint needed);

    [LibraryImport("advapi32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CloseServiceHandle(nint handle);
}
