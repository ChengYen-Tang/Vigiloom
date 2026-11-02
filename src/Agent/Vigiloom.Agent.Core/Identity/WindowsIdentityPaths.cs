using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Core.Identity;

/// <summary>Keeps the verified vault directory from being renamed or removed during a session.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsIdentityPaths
{
    internal static SafeFileHandle OpenDirectory(string path)
    {
        // FILE_READ_ATTRIBUTES; share read/write but not delete; OPEN_EXISTING;
        // FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT.
        var handle = CreateFile(path, 0x80, 0x03, 0, 3, 0x02200000, 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new IOException("Unable to protect the Agent identity directory.");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
}
