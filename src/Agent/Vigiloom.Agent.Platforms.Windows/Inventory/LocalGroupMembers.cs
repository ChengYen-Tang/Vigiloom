using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Inventory;

/// <summary>Reads direct local-group member SIDs without resolving foreign principals.</summary>
[SupportedOSPlatform("windows")]
internal static partial class LocalGroupMembers
{
    private const uint MoreData = 234;
    private const uint PageBytes = 64 * 1024;

    internal static (IReadOnlyList<string> Members, bool Partial, bool LimitReached) Read(
        string groupName, int remaining, CancellationToken token)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partial = false;
        var limitReached = false;
        nuint resume = 0;
        uint result;
        do
        {
            // Once enumeration starts, drain ERROR_MORE_DATA pages to release the RPC cursor.
            result = NetLocalGroupGetMembers(null, groupName, 0, out var buffer, PageBytes,
                out var count, out _, ref resume);
            try
            {
                if (result is not (0 or MoreData)) { partial = true; break; }
                for (uint i = 0; i < count && !limitReached && !token.IsCancellationRequested; i++)
                {
                    var pointer = Marshal.ReadIntPtr(buffer, checked((int)i * IntPtr.Size));
                    if (pointer == IntPtr.Zero) { partial = true; continue; }
                    string sid;
                    try { sid = new SecurityIdentifier(pointer).Value; }
                    catch (ArgumentException) { partial = true; continue; }
                    if (!members.Contains(sid) && members.Count >= Math.Min(remaining, InventoryChunkCodec.MaximumMembers))
                    {
                        partial = true;
                        limitReached = remaining <= InventoryChunkCodec.MaximumMembers;
                        break;
                    }
                    members.Add(sid);
                }
                if (result == MoreData && count == 0) { partial = true; break; }
            }
            finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        } while (result == MoreData);
        token.ThrowIfCancellationRequested();
        return (members.ToArray(), partial, limitReached);
    }

    [LibraryImport("Netapi32.dll", EntryPoint = "NetLocalGroupGetMembers", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint NetLocalGroupGetMembers(string? server, string group, uint level, out IntPtr buffer,
        uint preferredMaximumLength, out uint entriesRead, out uint totalEntries, ref nuint resumeHandle);

    [LibraryImport("Netapi32.dll")]
    private static partial uint NetApiBufferFree(IntPtr buffer);
}
