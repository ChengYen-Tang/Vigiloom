using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Baseline;
using Vigiloom.Contracts.Agent.Baseline;

namespace Vigiloom.Agent.Platforms.Linux.Baseline;

/// <summary>Inspects shadow metadata without reading account or password data.</summary>
[SupportedOSPlatform("linux")]
public static partial class ShadowPermissions
{
    public static BaselineObservation Read(string? distribution, string? version) => Read(distribution, version, "/etc/shadow");

    internal static BaselineObservation Read(string? distribution, string? version, string path)
    {
        if (string.IsNullOrWhiteSpace(distribution) || string.IsNullOrWhiteSpace(version)) return new(BaselineOutcome.Unknown);
        if (!((distribution == "ubuntu" && version is "22.04" or "24.04") || (distribution == "debian" && version == "12")))
            return new(BaselineOutcome.NotApplicable);
        try
        {
            if (Statx(-100, path, 0x100, 3, out var info) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                return new(error is 1 or 13 ? BaselineOutcome.PermissionDenied : BaselineOutcome.Unknown);
            }
            if ((info.Mask & 3) != 3 || (info.Mode & 0xf000) != 0x8000) return new(BaselineOutcome.Unknown);
            var mode = (UnixFileMode)(info.Mode & 0xfff);
            var acl = ExtendedAcl(path);
            if (acl < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                return new(error is 1 or 13 ? BaselineOutcome.PermissionDenied : BaselineOutcome.Unknown);
            }
            const UnixFileMode allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            var passed = acl == 0 && (mode & ~allowed) == 0;
            return new(passed ? BaselineOutcome.Pass : BaselineOutcome.Fail,
                $"Mode {Convert.ToString((int)mode, 8)}; extended ACL {(acl == 0 ? "absent" : "present")}");
        }
        catch (UnauthorizedAccessException) { return new(BaselineOutcome.PermissionDenied); }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException) { return new(BaselineOutcome.Unknown); }
    }

    [LibraryImport("libacl.so.1", EntryPoint = "acl_extended_file_nofollow", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int ExtendedAcl(string path);

    // Linux statx has a fixed 256-byte ABI; only the type and permission fields are needed.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out StatxBuffer result);
}
