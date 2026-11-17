using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using Vigiloom.BuildingBlocks.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Updater.Core.Transport;

/// <summary>A local request queue protected by the installer-owned Unix socket directory and peer UID.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxUpdateListener : ILocalUpdateListener
{
    private readonly Socket listener;
    private readonly FileStream ownership;
    private readonly string socketPath;
    private readonly uint agentUid;
    private readonly Channel<UpdateHandoff> requests = Channel.CreateBounded<UpdateHandoff>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private int disposed;

    private LinuxUpdateListener(Socket listener, FileStream ownership, string socketPath, uint agentUid) =>
        (this.listener, this.ownership, this.socketPath, this.agentUid) = (listener, ownership, socketPath, agentUid);

    public ChannelReader<UpdateHandoff> Requests => requests.Reader;

    /// <remarks>The installer creates the root-owned, non-writable directory with the Agent group and setgid bit.</remarks>
    public static LinuxUpdateListener Create(string directory, uint agentUid, uint agentGroupId)
    {
        if (GetEffectiveUserId() != 0 || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new UnauthorizedAccessException("The Linux Updater requires root on a supported architecture.");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute IPC directory is required.", nameof(directory));
        var full = Path.GetFullPath(directory);
        ValidateDirectory(full, agentGroupId);
        var socketPath = Path.Combine(full, "updater.sock");
        var ownership = new FileStream(Path.Combine(full, "updater.lock"), new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        Socket? socket = null;
        var bound = false;
        try
        {
            File.Delete(socketPath); // The exclusive owner removes only its own protected stale endpoint.
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            bound = true;
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            var endpoint = Stat(socketPath);
            if ((endpoint.Mode & 0xf000) != 0xc000 || endpoint.Owner != 0 || endpoint.Group != agentGroupId)
                throw new UnauthorizedAccessException("The updater socket ownership is invalid.");
            socket.Listen(4);
            return new LinuxUpdateListener(socket, ownership, socketPath, agentUid);
        }
        catch
        {
            socket?.Dispose();
            if (bound) File.Delete(socketPath);
            ownership.Dispose();
            throw;
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket peer;
                try { peer = await listener.AcceptAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { break; }
                catch (SocketException) when (Volatile.Read(ref disposed) != 0) { break; }
                using (peer)
                {
                    try
                    {
                        if (LinuxPeerCredentials.Read(peer).UserId != agentUid) continue;
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(15));
                        await using var stream = new NetworkStream(peer, ownsSocket: false);
                        var request = await BoundedJsonFrame.ReadAsync<UpdateHandoff>(stream, deadline.Token);
                        var status = !request.IsValid() ? UpdateHandoffStatus.Rejected :
                            requests.Writer.TryWrite(request) ? UpdateHandoffStatus.Accepted : UpdateHandoffStatus.Busy;
                        await BoundedJsonFrame.WriteAsync(stream, new UpdateHandoffResponse { Status = status }, deadline.Token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    { /* A disconnected, malformed or unauthorized local client cannot terminate the listener. */ }
                }
            }
        }
        finally { requests.Writer.TryComplete(); }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        listener.Dispose();
        try { File.Delete(socketPath); }
        finally { ownership.Dispose(); requests.Writer.TryComplete(); }
        return ValueTask.CompletedTask;
    }

    private static void ValidateDirectory(string path, uint group)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var rootStat = Stat(root);
        Check(rootStat);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            var stat = Stat(current);
            Check(stat);
            if (i == parts.Length - 1 && (stat.Group != group || (stat.Mode & 0x400) == 0))
                throw new UnauthorizedAccessException("The updater IPC directory must have the Agent group and setgid bit.");
        }
        if (parts.Length == 0) throw new ArgumentException("The filesystem root cannot be an IPC directory.", nameof(path));

        static void Check(Statx stat)
        {
            if ((stat.Mode & 0xf000) != 0x4000 || stat.Owner != 0 || (stat.Mode & 0x12) != 0)
                throw new UnauthorizedAccessException("The updater IPC directory or an ancestor is not root-owned and protected.");
        }
    }

    private static Statx Stat(string path)
    {
        const uint fields = 0x1b; // STATX_TYPE | STATX_MODE | STATX_UID | STATX_GID
        if (StatxCall(332, -100, path, 0x100, fields, out var result) != 0 || (result.Mask & fields) != fields)
            throw new IOException("Could not verify local IPC path ownership.");
        return result;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Owner;
        [FieldOffset(24)] public uint Group;
        [FieldOffset(28)] public ushort Mode;
    }

    [LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint StatxCall(nint number, int directory, string path, int flags, uint mask, out Statx result);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint GetEffectiveUserId();
}
