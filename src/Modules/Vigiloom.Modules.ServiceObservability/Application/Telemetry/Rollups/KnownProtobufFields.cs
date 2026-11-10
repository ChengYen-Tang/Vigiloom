using System.Buffers;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Finds unsupported protobuf fields without interpreting or discarding them.</summary>
internal static class KnownProtobufFields
{
    private const int MaximumPointBytes = 262_144;
    private const int MaximumDepth = 32;
    private const int MaximumFields = 100_000;
    internal const long MaximumWindowBytes = 16 * 1024 * 1024;

    internal enum Check { Known, Unsupported, Capacity }

    internal static Check Inspect(IMessage message, ref long remainingBytes)
    {
        ArgumentNullException.ThrowIfNull(message);
        var size = message.CalculateSize();
        if (size > MaximumPointBytes || size > remainingBytes) return Check.Capacity;
        remainingBytes -= size;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
        try
        {
            message.WriteTo(buffer.AsSpan(0, size));
            return InspectBytes(buffer.AsSpan(0, size), message.Descriptor);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    internal static Check InspectBytes(ReadOnlySpan<byte> bytes, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (bytes.Length > MaximumPointBytes) return Check.Capacity;
        var fields = MaximumFields;
        return Scan(bytes, descriptor, 0, ref fields);
    }

    private static Check Scan(ReadOnlySpan<byte> bytes, MessageDescriptor descriptor, int depth, ref int fields)
    {
        if (depth > MaximumDepth) return Check.Capacity;
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (--fields < 0) return Check.Capacity;
            if (!Varint(bytes, ref offset, out var tag) || tag == 0 || tag > uint.MaxValue) return Check.Unsupported;
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            var field = descriptor.FindFieldByNumber(number);
            if (field is null) return Check.Unsupported;
            var expected = WireType(field.FieldType);
            if (expected < 0 || wire != expected && !(wire == 2 && field.IsRepeated && IsPackable(field.FieldType)))
                return Check.Unsupported;
            switch (wire)
            {
                case 0:
                    if (!Varint(bytes, ref offset, out var numberValue) || !ValidInteger(field.FieldType, numberValue)) return Check.Unsupported;
                    break;
                case 1:
                    if (bytes.Length - offset < 8) return Check.Unsupported;
                    offset += 8;
                    break;
                case 2:
                    if (!Varint(bytes, ref offset, out var length) || length > (ulong)(bytes.Length - offset)) return Check.Unsupported;
                    var span = bytes.Slice(offset, (int)length);
                    if (field.FieldType == FieldType.Message)
                    {
                        var nested = Scan(span, field.MessageType, depth + 1, ref fields);
                        if (nested != Check.Known) return nested;
                    }
                    else if (field.IsRepeated && IsPackable(field.FieldType))
                    {
                        var packed = InspectPacked(span, field.FieldType, expected, ref fields);
                        if (packed != Check.Known) return packed;
                    }
                    offset += (int)length;
                    break;
                case 5:
                    if (bytes.Length - offset < 4) return Check.Unsupported;
                    offset += 4;
                    break;
                default:
                    return Check.Unsupported;
            }
        }
        return Check.Known;
    }

    private static Check InspectPacked(ReadOnlySpan<byte> bytes, FieldType type, int expected, ref int fields)
    {
        if (expected is 1 or 5)
        {
            var width = expected == 1 ? 8 : 4;
            if (bytes.Length % width != 0) return Check.Unsupported;
            var count = bytes.Length / width;
            if (count > fields) return Check.Capacity;
            fields -= count;
            return Check.Known;
        }
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (--fields < 0) return Check.Capacity;
            if (!Varint(bytes, ref offset, out var value) || !ValidInteger(type, value)) return Check.Unsupported;
        }
        return Check.Known;
    }

    private static bool ValidInteger(FieldType type, ulong value) => type switch
    {
        FieldType.UInt32 or FieldType.SInt32 => value <= uint.MaxValue,
        FieldType.Int32 or FieldType.Enum => value <= int.MaxValue || value >= 0xffffffff80000000UL,
        _ => true
    };

    private static bool Varint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
    {
        value = 0;
        for (var i = 0; i < 10 && offset < bytes.Length; i++)
        {
            var current = bytes[offset++];
            if (i == 9 && current > 1) return false;
            value |= (ulong)(current & 0x7f) << (i * 7);
            if ((current & 0x80) == 0) return true;
        }
        return false;
    }

    private static bool IsPackable(FieldType type) => type is not (FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group);
    private static int WireType(FieldType type) => type switch
    {
        FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64 => 1,
        FieldType.String or FieldType.Bytes or FieldType.Message => 2,
        FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32 => 5,
        FieldType.Group => -1,
        _ => 0
    };
}
