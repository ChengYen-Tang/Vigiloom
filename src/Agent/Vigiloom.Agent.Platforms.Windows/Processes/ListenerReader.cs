using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Processes;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Processes;

[SupportedOSPlatform("windows")]
public static partial class ListenerReader
{
    private const uint InsufficientBuffer = 122;
    private const int MaximumBytes = 16 * 1024 * 1024;

    public static InventoryReadResult<ListenerObservation> Read(CancellationToken token)
    {
        var result = new List<ListenerObservation>();
        var reasons = (InventoryPartialReason)0;
        foreach (var (tcp, family) in new[] { (true, 2), (true, 23), (false, 2), (false, 23) })
        {
            token.ThrowIfCancellationRequested();
            reasons |= ReadTable(tcp, family, result, token);
        }
        return new(reasons == 0 ? InventoryReadStatus.Complete : InventoryReadStatus.Partial, result, reasons == 0 ? null : reasons);
    }

    private static unsafe InventoryPartialReason ReadTable(bool tcp, int family, List<ListenerObservation> output, CancellationToken token)
    {
        uint size = 0;
        var code = Query(tcp, null, ref size, family);
        if (code != InsufficientBuffer) return ErrorReason(code);
        if (size is < 4 or > MaximumBytes) return size > MaximumBytes ? InventoryPartialReason.Truncated : InventoryPartialReason.Other;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (size > MaximumBytes) return InventoryPartialReason.Truncated;
            var buffer = new byte[size];
            uint actual = size;
            fixed (byte* pointer = buffer) code = Query(tcp, pointer, ref actual, family);
            if (code == InsufficientBuffer) { size = actual; continue; }
            if (code != 0) return ErrorReason(code);
            if (actual > buffer.Length || actual < 4) return InventoryPartialReason.Other;
            var data = buffer.AsSpan(0, (int)actual);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
            var rowSize = (tcp, family) switch { (true, 2) => 24, (true, 23) => 56, (false, 2) => 12, _ => 28 };
            if (count > 20_000 || output.Count + count > 20_000) return InventoryPartialReason.Truncated;
            if (count > (data.Length - 4) / rowSize) return InventoryPartialReason.Other;
            var ownersKnown = true;
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var row = data.Slice(4 + (int)index * rowSize, rowSize);
                var addressOffset = family == 2 && tcp ? 4 : 0;
                var portOffset = family == 2 ? (tcp ? 8 : 4) : 20;
                var pidOffset = rowSize - 4;
                var address = family == 2 ? new IPAddress(row.Slice(addressOffset, 4)) :
                    new IPAddress(row[..16], BinaryPrimitives.ReadUInt32BigEndian(row.Slice(16, 4)));
                var port = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(portOffset, 2));
                var pid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(pidOffset, 4));
                if (port == 0) continue;
                var owner = ParseOwner(pid);
                if (owner is null) ownersKnown = false;
                output.Add(new(tcp ? "tcp" : "udp", address.ToString(), port, owner));
            }
            return ownersKnown ? 0 : InventoryPartialReason.OwnerUnavailable;
        }
        return InventoryPartialReason.Other;
    }

    private static InventoryPartialReason ErrorReason(uint error) => error == 5 ? InventoryPartialReason.AccessDenied : InventoryPartialReason.Other;

    internal static int? ParseOwner(uint pid) => pid is > 0 and <= int.MaxValue ? (int)pid : null;

    private static unsafe uint Query(bool tcp, byte* buffer, ref uint size, int family) => tcp
        ? GetExtendedTcpTable(buffer, ref size, false, (uint)family, 3, 0)
        : GetExtendedUdpTable(buffer, ref size, false, (uint)family, 1, 0);

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetExtendedTcpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetExtendedUdpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
}
