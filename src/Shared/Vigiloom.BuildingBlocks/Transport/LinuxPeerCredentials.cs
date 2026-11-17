using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigiloom.BuildingBlocks.Transport;

public readonly record struct UnixPeerCredentials(int ProcessId, uint UserId, uint GroupId);

/// <summary>Reads the kernel-authenticated peer identity of a connected Linux Unix-domain socket.</summary>
[SupportedOSPlatform("linux")]
public static partial class LinuxPeerCredentials
{
    public static UnixPeerCredentials Read(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (socket.AddressFamily != AddressFamily.Unix || !socket.Connected)
            throw new ArgumentException("A connected Unix-domain socket is required.", nameof(socket));
        var handle = socket.SafeHandle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var length = (uint)Marshal.SizeOf<Credentials>();
            if (GetSocketOption(handle.DangerousGetHandle().ToInt32(), 1, 17, out var credentials, ref length) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read local peer credentials.");
            if (length != Marshal.SizeOf<Credentials>() || credentials.ProcessId <= 0)
                throw new InvalidDataException("The local peer credentials are invalid.");
            return new(credentials.ProcessId, credentials.UserId, credentials.GroupId);
        }
        finally { if (added) handle.DangerousRelease(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials { public int ProcessId; public uint UserId, GroupId; }

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetSocketOption(int socket, int level, int option, out Credentials credentials, ref uint length);
}
