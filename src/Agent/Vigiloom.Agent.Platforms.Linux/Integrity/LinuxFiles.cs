using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Platforms.Linux.Integrity;

/// <summary>Linux x64 handle-relative access using openat2, statx and getdents64.</summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxFiles
{
    private const int OpenAt2Number = 437, GetDents64Number = 217, CurrentDirectory = -100;
    private const int Directory = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000, PathOnly = 0x200000, NonBlocking = 0x800;
    private const ulong NoCrossMount = 0x01, NoMagicLinks = 0x02, NoSymlinks = 0x04, Beneath = 0x08;
    private const int EmptyPath = 0x1000;
    private const uint Required = 0x7ff;
    internal const ushort TypeMask = 0xf000, DirectoryType = 0x4000, FileType = 0x8000, SymlinkType = 0xa000;

    internal readonly record struct Stamp(ushort Mode, uint Uid, uint Gid, ulong Inode, ulong Size,
        uint DeviceMajor, uint DeviceMinor, long ModifiedSeconds, uint ModifiedNanos, long ChangedSeconds, uint ChangedNanos);

    internal static SafeFileHandle? OpenRoot(string path, out int error) => Open(CurrentDirectory, path, Directory | CloseOnExec, NoMagicLinks | NoSymlinks, out error);
    internal static bool IsLocal(SafeFileHandle handle, out int error)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (FstatFs(checked((int)handle.DangerousGetHandle()), out var value) != 0) { error = Marshal.GetLastPInvokeError(); return false; }
            error = 0;
            // Known local filesystems only; unknown and remote filesystems fail closed.
            return value.Type is 0xef53 or 0x58465342 or 0x9123683e or 0x2fc12fc1 or 0x01021994 or 0x794c7630;
        }
        finally { if (added) handle.DangerousRelease(); }
    }
    internal static SafeFileHandle? OpenChild(SafeFileHandle parent, string name, bool directory, bool inspectOnly, out int error)
    {
        var added = false;
        try
        {
            parent.DangerousAddRef(ref added);
            // O_PATH | O_NOFOLLOW permits inspecting the final symlink itself. Intermediate
            // symlinks remain forbidden by RESOLVE_NO_SYMLINKS and must be treated as a race.
            var flags = CloseOnExec | (inspectOnly ? PathOnly | NoFollow : NonBlocking) | (directory ? Directory : 0);
            return Open(checked((int)parent.DangerousGetHandle()), name, flags, Beneath | NoCrossMount | NoMagicLinks | NoSymlinks, out error);
        }
        finally { if (added) parent.DangerousRelease(); }
    }

    private static unsafe SafeFileHandle? Open(int parent, string path, int flags, ulong resolve, out int error)
    {
        var how = new OpenHow { Flags = (ulong)flags, Resolve = resolve };
        var fd = OpenAt2(OpenAt2Number, parent, path, &how, (nuint)sizeof(OpenHow));
        if (fd < 0) { error = Marshal.GetLastPInvokeError(); return null; }
        error = 0;
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    internal static bool TryStamp(SafeFileHandle handle, out Stamp stamp, out int error)
    {
        stamp = default;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = checked((int)handle.DangerousGetHandle());
            if (Statx(fd, "", EmptyPath, Required, out var value) != 0) { error = Marshal.GetLastPInvokeError(); return false; }
            if ((value.Mask & Required) != Required) { error = 0; return false; }
            stamp = new(value.Mode, value.Uid, value.Gid, value.Inode, value.Size, value.DeviceMajor, value.DeviceMinor,
                value.ModifiedSeconds, value.ModifiedNanos, value.ChangedSeconds, value.ChangedNanos);
            error = 0;
            return true;
        }
        finally { if (added) handle.DangerousRelease(); }
    }

    internal static bool DirectoryMatches(SafeFileHandle root, string relativePath, Stamp observed)
    {
        using var directory = OpenChild(root, relativePath, directory: true, inspectOnly: false, out _);
        return directory is not null && TryStamp(directory, out var current, out _) && current == observed;
    }

    internal static unsafe int ReadEntries(SafeFileHandle directory, byte[] buffer, out int error)
    {
        var added = false;
        try
        {
            directory.DangerousAddRef(ref added);
            fixed (byte* pointer = buffer)
            {
                var result = GetDents(GetDents64Number, checked((int)directory.DangerousGetHandle()), pointer, (nuint)buffer.Length);
                if (result < 0) { error = Marshal.GetLastPInvokeError(); return -1; }
                error = 0;
                return checked((int)result);
            }
        }
        finally { if (added) directory.DangerousRelease(); }
    }

    internal static unsafe byte[]? ReadAcl(SafeFileHandle handle, bool defaultAcl, out int error)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = checked((int)handle.DangerousGetHandle());
            var name = defaultAcl ? "system.posix_acl_default" : "system.posix_acl_access";
            var size = Fgetxattr(fd, name, null, 0);
            if (size < 0)
            {
                error = Marshal.GetLastPInvokeError();
                return error is 61 or 95 ? [] : null; // ENODATA or ENOTSUP: no extended ACL
            }
            if (size > 64 * 1024) { error = 7; return null; }
            var bytes = new byte[(int)size];
            fixed (byte* pointer = bytes)
            {
                var actual = Fgetxattr(fd, name, pointer, (nuint)bytes.Length);
                if (actual < 0 || actual != size) { error = actual < 0 ? Marshal.GetLastPInvokeError() : 34; return null; }
            }
            error = 0;
            return bytes;
        }
        finally { if (added) handle.DangerousRelease(); }
    }

    internal static bool TryEntry(ReadOnlySpan<byte> buffer, ref int offset, out ReadOnlySpan<byte> name)
    {
        name = default;
        if (offset < 0 || buffer.Length - offset < 20) return false;
        var record = buffer[offset..];
        var length = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
        if (length < 20 || length > record.Length) return false;
        var field = record.Slice(19, length - 19);
        var end = field.IndexOf((byte)0);
        if (end < 1) return false;
        name = field[..end];
        offset += length;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { public ulong Flags; public ulong Mode; public ulong Resolve; }
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct StatFs { [FieldOffset(0)] public long Type; }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(24)] public uint Gid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanos;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanos;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial nint OpenAt2(nint number, int directory, string path, OpenHow* how, nuint size);
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial nint GetDents(nint number, int directory, byte* buffer, nuint size);
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Statx(int directory, string path, int flags, uint mask, out StatxBuffer result);
    [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int FstatFs(int descriptor, out StatFs value);
    [LibraryImport("libc", EntryPoint = "fgetxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial nint Fgetxattr(int descriptor, string name, byte* value, nuint size);
}
