using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Platforms.Windows.Baseline;

/// <summary>Reads the local computer role without querying remote systems.</summary>
[SupportedOSPlatform("windows")]
internal static partial class MachineRole
{
    internal static (int? Role, bool PermissionDenied) Read()
    {
        try
        {
            var result = GetPrimaryDomainInformation(null, 1, out var buffer);
            if (result != 0) return (null, result is 5 or 1314);
            if (buffer == IntPtr.Zero) return (null, false);
            try
            {
                var role = Marshal.ReadInt32(buffer);
                return role is >= 0 and <= 5 ? (role, false) : (null, false);
            }
            finally { FreeMemory(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return (null, false); }
    }

    [LibraryImport("netapi32.dll", EntryPoint = "DsRoleGetPrimaryDomainInformation", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint GetPrimaryDomainInformation(string? server, int level, out IntPtr buffer);

    [LibraryImport("netapi32.dll", EntryPoint = "DsRoleFreeMemory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial void FreeMemory(IntPtr buffer);
}
