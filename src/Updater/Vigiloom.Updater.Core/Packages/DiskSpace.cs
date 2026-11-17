using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vigiloom.Updater.Core.Packages;

internal static partial class DiskSpace
{
    public static ulong Available(string directory)
    {
        if (!OperatingSystem.IsWindows()) return checked((ulong)new DriveInfo(directory).AvailableFreeSpace);
        var path = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        if (GetDiskFreeSpaceEx(path, out var available, IntPtr.Zero, IntPtr.Zero) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return available;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetDiskFreeSpaceEx(string directory, out ulong available, IntPtr totalBytes, IntPtr freeBytes);
}
