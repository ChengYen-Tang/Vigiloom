using Google.Protobuf.Reflection;

namespace Vigiloom.Web.Telemetry;

/// <summary>Bounds protobuf object expansion before the official parser allocates its message graph.</summary>
internal static class ProtobufBudget
{
    private const int MaximumDepth = 32;
    private const int MaximumFields = 100_000;

    internal static bool Allows(ReadOnlySpan<byte> data, MessageDescriptor descriptor)
    {
        var remaining = MaximumFields;
        return Scan(data, descriptor, 0, ref remaining);
    }

    private static bool Scan(ReadOnlySpan<byte> data, MessageDescriptor descriptor, int depth, ref int remaining)
    {
        if (depth > MaximumDepth) return false;
        var offset = 0;
        while (offset < data.Length)
        {
            if (--remaining < 0 || !Varint(data, ref offset, out var tag) || tag > uint.MaxValue || (tag >> 3) == 0) return false;
            var number = (int)(tag >> 3);
            var field = descriptor.FindFieldByNumber(number);
            switch (tag & 7)
            {
                case 0:
                    if (!Varint(data, ref offset, out _)) return false;
                    break;
                case 1:
                    if (!Advance(data, ref offset, 8)) return false;
                    break;
                case 2:
                    if (!Varint(data, ref offset, out var size) || size > (ulong)(data.Length - offset)) return false;
                    var slice = data.Slice(offset, (int)size);
                    offset += (int)size;
                    if (field?.FieldType == FieldType.Message)
                    {
                        if (!Scan(slice, field.MessageType, depth + 1, ref remaining)) return false;
                    }
                    else if (field is { IsRepeated: true } && IsPackable(field.FieldType))
                    {
                        if (!Packed(slice, field.FieldType, ref remaining)) return false;
                    }
                    break;
                case 5:
                    if (!Advance(data, ref offset, 4)) return false;
                    break;
                default:
                    // OTLP schemas have no protobuf groups.
                    return false;
            }
        }
        return true;
    }

    private static bool Packed(ReadOnlySpan<byte> data, FieldType type, ref int remaining)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (--remaining < 0) return false;
            if (type is FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64)
            { if (!Advance(data, ref offset, 8)) return false; }
            else if (type is FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32)
            { if (!Advance(data, ref offset, 4)) return false; }
            else if (!Varint(data, ref offset, out _)) return false;
        }
        return true;
    }

    private static bool IsPackable(FieldType type) => type is not (FieldType.Message or FieldType.Group or FieldType.String or FieldType.Bytes);

    private static bool Advance(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        if (length > data.Length - offset) return false;
        offset += length;
        return true;
    }

    private static bool Varint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset == data.Length) return false;
            var next = data[offset++];
            if (shift == 63 && next > 1) return false;
            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) return true;
        }
        return false;
    }
}
