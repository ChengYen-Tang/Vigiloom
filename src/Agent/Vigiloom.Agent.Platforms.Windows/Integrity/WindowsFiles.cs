using System.ComponentModel;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Platforms.Windows.Integrity;

/// <summary>Opens single names relative to an already verified directory handle.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsFiles
{
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    internal const uint Directory = 0x10, ReparsePoint = 0x400;
    private const uint ReadAccess = 0x00120081, ShareAll = 7, OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const int FileOpen = 1, SynchronousNonAlert = 0x20;

    internal static SafeFileHandle OpenVolume(string drive)
    {
        if (drive.Length != 3 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':' || drive[2] != '\\' || GetDriveType(drive) != 3)
            throw new PlatformNotSupportedException("A fixed local drive is required.");
        var handle = CreateFile(drive, ReadAccess, ShareAll, nint.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, nint.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }

    internal static SafeFileHandle OpenScope(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new PlatformNotSupportedException("A drive-qualified local path is required.");
        var full = Path.GetFullPath(path);
        var drive = Path.GetPathRoot(full) ?? throw new ArgumentException("A local absolute path is required.", nameof(path));
        var current = OpenVolume(drive);
        try
        {
            var root = ReadStamp(current);
            if ((root.Attributes & (Directory | ReparsePoint)) != Directory) throw new IOException("The drive root is not a regular directory.");
            foreach (var name in full[drive.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var child = OpenChild(current, name, ignoreCase: true);
                try
                {
                    var stamp = ReadStamp(child);
                    if ((stamp.Attributes & (Directory | ReparsePoint)) != Directory || stamp.Volume != root.Volume)
                        throw new IOException("A scope component is a reparse point, a different volume, or not a directory.");
                }
                catch { child.Dispose(); throw; }
                current.Dispose();
                current = child;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    internal static unsafe SafeFileHandle OpenChild(SafeFileHandle parent, string name, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0 || name.Length > 255)
            throw new ArgumentException("A single filesystem name is required.", nameof(name));
        var added = false;
        try
        {
            parent.DangerousAddRef(ref added);
            fixed (char* characters = name)
            {
                var text = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = (nint)characters };
                var attributes = new ObjectAttributes { Length = (uint)sizeof(ObjectAttributes), Root = parent.DangerousGetHandle(), Name = (nint)(&text), Attributes = 0x1000u | (ignoreCase ? 0x40u : 0u) };
                var status = NtCreateFile(out var raw, ReadAccess, &attributes, out _, nint.Zero, 0, ShareAll, FileOpen,
                    SynchronousNonAlert | (int)OpenReparsePoint, nint.Zero, 0);
                if (status < 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
                return new SafeFileHandle(raw, ownsHandle: true);
            }
        }
        finally { if (added) parent.DangerousRelease(); }
    }

    internal static Stamp ReadStamp(SafeFileHandle handle)
    {
        if (GetFileType(handle) != 1) throw new IOException("A local disk object is required.");
        if (!GetFileInformation(handle, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!GetFileId(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdInfo>())) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!GetFileBasic(handle, 0, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>())) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new(info.Attributes, id.Volume, id.Low, id.High, ((ulong)info.SizeHigh << 32) | info.SizeLow,
            basic.LastWrite, basic.Change);
    }

    internal static (string Digest, string Owner) ReadSecurity(SafeFileHandle handle)
    {
        var error = GetSecurityInfo(handle, 1, 5, out var owner, nint.Zero, nint.Zero, nint.Zero, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            if (owner == nint.Zero || descriptor == nint.Zero) throw new InvalidDataException("Owner or security descriptor is unavailable.");
            var size = GetSecurityDescriptorLength(descriptor);
            if (size is 0 or > 65536) throw new InvalidDataException("The security descriptor exceeds the collection limit.");
            var bytes = new byte[size];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return (Convert.ToHexStringLower(SHA256.HashData(bytes)), new SecurityIdentifier(owner).Value);
        }
        finally { if (descriptor != nint.Zero) _ = LocalFree(descriptor); }
    }

    /// <summary>Reads at most one bounded native directory buffer; null denotes end of enumeration.</summary>
    internal static unsafe List<string>? ReadNames(SafeFileHandle directory, byte[] buffer, bool restart)
    {
        if (buffer.Length < 1024) throw new ArgumentException("The directory buffer is too small.", nameof(buffer));
        fixed (byte* pointer = buffer)
        {
            if (!GetDirectoryInfo(directory, restart ? 11 : 10, pointer, (uint)buffer.Length))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 18) return null; // ERROR_NO_MORE_FILES
                throw new Win32Exception(error);
            }
        }
        var result = new List<string>();
        var offset = 0;
        while (true)
        {
            var record = buffer.AsSpan(offset);
            if (record.Length < 104) throw new InvalidDataException("Invalid directory record.");
            var next = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var bytes = BinaryPrimitives.ReadUInt32LittleEndian(record[60..]);
            var length = next == 0 ? record.Length : checked((int)next);
            if (length < 104 || length > record.Length || (next != 0 && (next & 7) != 0) ||
                (bytes & 1) != 0 || bytes > length - 104) throw new InvalidDataException("Invalid directory record.");
            var name = StrictUnicode.GetString(record.Slice(104, (int)bytes));
            if (name is not ("." or "..")) result.Add(name);
            if (next == 0) break;
            offset += length;
        }
        return result;
    }

    internal readonly record struct Stamp(uint Attributes, ulong Volume, ulong IdLow, ulong IdHigh, ulong Size, long LastWrite, long Change);
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public uint Length; public nint Root, Name; public uint Attributes; public nint Security, Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public nint Status; public nuint Information; }
    [StructLayout(LayoutKind.Explicit, Size = 52)] private struct FileInfo { [FieldOffset(0)] public uint Attributes; [FieldOffset(32)] public uint SizeHigh; [FieldOffset(36)] public uint SizeLow; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct FileIdInfo { [FieldOffset(0)] public ulong Volume; [FieldOffset(8)] public ulong Low; [FieldOffset(16)] public ulong High; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct FileBasicInfo { [FieldOffset(16)] public long LastWrite; [FieldOffset(24)] public long Change; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint GetDriveType(string root);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)] private static partial uint GetFileType(SafeFileHandle file);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformation(SafeFileHandle file, out FileInfo info);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileId(SafeFileHandle file, int type, out FileIdInfo info, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileBasic(SafeFileHandle file, int type, out FileBasicInfo info, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetDirectoryInfo(SafeFileHandle file, int type, byte* info, uint size);
    [LibraryImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static unsafe partial int NtCreateFile(out nint file, uint access, ObjectAttributes* attributes, out IoStatus status,
        nint allocation, uint fileAttributes, uint share, int disposition, int options, nint ea, uint eaLength);
    [LibraryImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError")] private static partial uint RtlNtStatusToDosError(int status);
    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityInfo")]
    private static partial uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out nint owner,
        nint group, nint dacl, nint sacl, out nint descriptor);
    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength")] private static partial uint GetSecurityDescriptorLength(nint descriptor);
    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")] private static partial nint LocalFree(nint memory);
}
