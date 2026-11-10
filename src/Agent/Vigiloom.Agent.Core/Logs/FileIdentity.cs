using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Core.Logs;

/// <summary>Identity of an opened file, not a guarantee against file-ID reuse over time.</summary>
public sealed record FileIdentity(string Platform, ulong Device, ulong FileId, ulong FileIdHigh, long? BirthSeconds, uint? BirthNanoseconds)
{
    public static FileIdentity FromHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows()) return WindowsFileIdentity.Read(handle);
        if (OperatingSystem.IsLinux()) return LinuxFileIdentity.Read(handle);
        throw new PlatformNotSupportedException("File log collection requires Windows or Linux.");
    }
}

internal static partial class WindowsFileIdentity
{
    internal static FileIdentity Read(SafeFileHandle handle)
    {
        if (GetFileType(handle) != 1) throw new IOException("A disk log file is required.");
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!GetFileInformationByHandleEx(handle, 18, out var id, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        const uint Directory = 0x10;
        if ((info.Attributes & Directory) != 0) throw new IOException("A regular log file is required.");
        var creation = ((ulong)info.CreationHigh << 32) | info.CreationLow;
        return new("windows", id.Volume, id.Low, id.High,
            creation == 0 ? null : checked((long)(creation / 10_000_000)),
            creation == 0 ? null : checked((uint)(creation % 10_000_000) * 100));
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct FileInformation
    {
        [FieldOffset(0)] public uint Attributes;
        [FieldOffset(4)] public uint CreationLow;
        [FieldOffset(8)] public uint CreationHigh;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdInformation
    {
        [FieldOffset(0)] public ulong Volume;
        [FieldOffset(8)] public ulong Low;
        [FieldOffset(16)] public ulong High;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdInformation information, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static partial uint GetFileType(SafeFileHandle handle);
}

internal static partial class LinuxFileIdentity
{
    internal static FileStream OpenRead(string path, int bufferSize)
    {
        // O_NONBLOCK prevents a FIFO from blocking before the opened handle is classified.
        var fd = Open(path, 0x800 | 0x80000); // O_RDONLY | O_NONBLOCK | O_CLOEXEC
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
        try { return new FileStream(handle, FileAccess.Read, bufferSize, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    internal static FileIdentity Read(SafeFileHandle handle)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Linux file identity requires x64.");
        const uint TypeAndInode = 0x101, BirthTime = 0x800;
        var addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            var descriptor = checked((int)handle.DangerousGetHandle());
            if (Statx(332, descriptor, "", 0x1000, TypeAndInode | BirthTime, out var stat) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if ((stat.Mask & TypeAndInode) != TypeAndInode || (stat.Mode & 0xf000) != 0x8000)
                throw new IOException("A regular file with a stable inode is required.");
            return new("linux", ((ulong)stat.DeviceMajor << 32) | stat.DeviceMinor, stat.Inode, 0,
                (stat.Mask & BirthTime) != 0 ? stat.BirthSeconds : null,
                (stat.Mask & BirthTime) != 0 ? stat.BirthNanoseconds : null);
        }
        finally { if (addedRef) handle.DangerousRelease(); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint Statx(nint number, int fd, string path, int flags, uint mask, out StatxResult result);
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
}
