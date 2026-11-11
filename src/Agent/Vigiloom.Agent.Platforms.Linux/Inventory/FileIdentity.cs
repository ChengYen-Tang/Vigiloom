using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Platforms.Linux.Inventory;

/// <summary>Compares path and open-file identity without following a replacement silently.</summary>
[SupportedOSPlatform("linux")]
internal static partial class FileIdentity
{
    private const int AtCurrentDirectory = -100;
    private const int AtEmptyPath = 0x1000;
    private const uint BasicStats = 0x7ff;
    private const uint Required = 0x3c0; // mtime, ctime, inode and size

    internal static bool TryRead(SafeFileHandle handle, string path, out Stamp stamp)
    {
        stamp = default;
        var addRef = false;
        try
        {
            handle.DangerousAddRef(ref addRef);
            var fd = checked((int)handle.DangerousGetHandle());
            if (Statx(fd, "", AtEmptyPath, BasicStats, out var open) != 0 ||
                Statx(AtCurrentDirectory, path, 0, BasicStats, out var current) != 0 ||
                (open.Mask & Required) != Required || (current.Mask & Required) != Required) return false;
            var a = ToStamp(open);
            var b = ToStamp(current);
            if (a != b) return false;
            stamp = a;
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
        finally { if (addRef) handle.DangerousRelease(); }
    }

    private static Stamp ToStamp(StatxBuffer value) => new(value.Inode, value.DeviceMajor, value.DeviceMinor,
        value.Size, value.ChangeSeconds, value.ChangeNanoseconds, value.ModifySeconds, value.ModifyNanoseconds);

    internal readonly record struct Stamp(ulong Inode, uint DeviceMajor, uint DeviceMinor, ulong Size,
        long ChangeSeconds, uint ChangeNanoseconds, long ModifySeconds, uint ModifyNanoseconds);

    // Linux UAPI struct statx is 256 bytes, with fixed-width fields across supported architectures.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long ChangeSeconds;
        [FieldOffset(104)] public uint ChangeNanoseconds;
        [FieldOffset(112)] public long ModifySeconds;
        [FieldOffset(120)] public uint ModifyNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out StatxBuffer result);
}
