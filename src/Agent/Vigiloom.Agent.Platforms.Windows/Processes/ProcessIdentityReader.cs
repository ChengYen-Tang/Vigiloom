using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Vigiloom.Agent.Core.Processes;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Processes;

[SupportedOSPlatform("windows")]
public static partial class ProcessIdentityReader
{
    public sealed record Details(string? Account, string? ExecutablePath, InventoryPartialReason? PartialReason = null);

    /// <summary>Reads an account SID only after verifying that the PID still denotes the observed process.</summary>
    public static string? ReadAccount(ProcessObservation observation) => Read(observation, false).Account;

    public static unsafe Details Read(ProcessObservation observation, bool includeExecutablePath)
    {
        using var process = OpenProcess(0x1000, false, (uint)observation.ProcessId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return new(null, null, NativeReason());
        if (!GetProcessTimes(process, out var creation, out _, out _, out _)) return new(null, null, NativeReason());
        if (creation != observation.StartedAt.UtcDateTime.ToFileTimeUtc()) return new(null, null, InventoryPartialReason.Other);
        string? account = null;
        InventoryPartialReason? reason = null;
        var tokenOpened = OpenProcessToken(process, 0x0008, out var token); // TOKEN_QUERY
        if (!tokenOpened) reason = NativeReason();
        using (token)
        {
            if (tokenOpened)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    account = identity.User?.Value;
        }
        string? path = null;
        if (includeExecutablePath)
        {
            var buffer = new char[2049];
            uint length = (uint)buffer.Length;
            fixed (char* text = buffer)
            {
                if (QueryFullProcessImageName(process, 0, text, ref length))
                {
                    if (length is > 0 and <= 2000) path = new string(buffer, 0, (int)length);
                    else reason = (reason ?? 0) | InventoryPartialReason.Other;
                }
                else reason = (reason ?? 0) | NativeReason();
            }
            if (path?.Any(char.IsControl) == true) path = null;
        }
        return new(account, path, reason);
    }

    private static InventoryPartialReason NativeReason() => Marshal.GetLastPInvokeError() == 5
        ? InventoryPartialReason.AccessDenied : InventoryPartialReason.Other;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* path, ref uint length);
}
