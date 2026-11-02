using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Core.Identity;

/// <summary>Linux x64 statx checks; fails closed when ownership or path type cannot be established.</summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxIdentityPaths
{
    // Linux x64 syscall table and struct statx UAPI: arch/x86/entry/syscalls/syscall_64.tbl and include/uapi/linux/stat.h.
    private const int StatxNumber = 332, CurrentDirectory = -100, NoFollow = 0x100;
    private const uint RequiredFields = 0x0b; // STATX_TYPE | STATX_MODE | STATX_UID
    private const ushort TypeMask = 0xf000, DirectoryType = 0x4000, FileType = 0x8000;
    private const ushort GroupOrOtherWrite = 0x12, Sticky = 0x200, PrivateDirectoryMode = 0x1c0, PrivateFileMask = 0x3f;

    internal static void PrepareDirectory(string path)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Agent identity storage requires Linux x64.");
        var currentUser = GetEffectiveUserId();
        var root = Path.GetPathRoot(path) ?? throw new InvalidOperationException("An absolute path is required.");
        var current = root;
        ValidateDirectory(current, currentUser, isLeaf: false);
        var segments = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!Directory.Exists(current)) Directory.CreateDirectory(current, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ValidateDirectory(current, currentUser, isLeaf: index == segments.Length - 1);
        }
    }

    internal static void ValidateFile(string path)
    {
        var stat = Read(path);
        if ((stat.Mode & TypeMask) != FileType || stat.Owner != GetEffectiveUserId() || (stat.Mode & PrivateFileMask) != 0)
            throw new UnauthorizedAccessException("The Agent identity file must be owned by the service account and private.");
    }

    private static void ValidateDirectory(string path, uint currentUser, bool isLeaf)
    {
        var stat = Read(path);
        var writable = (stat.Mode & GroupOrOtherWrite) != 0;
        var safeStickyParent = !isLeaf && stat.Owner == 0 && (stat.Mode & Sticky) != 0;
        if ((stat.Mode & TypeMask) != DirectoryType || (isLeaf ? stat.Owner != currentUser : stat.Owner != currentUser && stat.Owner != 0) ||
            (writable && !safeStickyParent) || (isLeaf && (stat.Mode & 0x0fff) != PrivateDirectoryMode))
            throw new UnauthorizedAccessException("Agent identity path ownership or permissions are unsafe.");
    }

    private static Statx Read(string path)
    {
        if (StatxCall(StatxNumber, CurrentDirectory, path, NoFollow, RequiredFields, out var result) != 0 || (result.Mask & RequiredFields) != RequiredFields)
            throw new IOException("Unable to establish Agent identity path ownership.");
        return result;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Owner;
        [FieldOffset(28)] public ushort Mode;
    }

    [LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint StatxCall(nint number, int directory, string path, int flags, uint mask, out Statx result);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint GetEffectiveUserId();
}
