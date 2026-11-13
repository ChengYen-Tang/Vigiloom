using System.Globalization;
using System.Text;
using OpenTelemetry.Proto.Common.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Formats OTLP values without materializing unbounded JSON or binary data.</summary>
internal static class LogBodyFormatter
{
    private const int MaximumCharacters = 2000;
    private const int MaximumItems = 64;
    private const int MaximumDepth = 8;

    internal static (string Text, bool Truncated) Format(AnyValue? value)
    {
        var builder = new StringBuilder();
        var truncated = false;
        var remainingItems = MaximumItems;
        void Append(string text)
        {
            var count = Math.Min(text.Length, MaximumCharacters - builder.Length);
            if (count > 0 && count < text.Length && char.IsHighSurrogate(text[count - 1]) && char.IsLowSurrogate(text[count])) count--;
            if (count > 0) builder.Append(text, 0, count);
            if (count < text.Length) truncated = true;
        }
        void AppendQuoted(string text)
        {
            Append("\"");
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (builder.Length >= MaximumCharacters) { truncated = true; break; }
                if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    if (builder.Length + 2 > MaximumCharacters) { truncated = true; break; }
                    builder.Append(character); builder.Append(text[++index]); continue;
                }
                switch (character)
                {
                    case '"': Append("\\\""); break;
                    case '\\': Append("\\\\"); break;
                    case '\n': Append("\\n"); break;
                    case '\r': Append("\\r"); break;
                    case '\t': Append("\\t"); break;
                    case < ' ': Append($"\\u{(int)character:x4}"); break;
                    default: builder.Append(character); break;
                }
            }
            Append("\"");
        }
        void Write(AnyValue? current, int depth)
        {
            if (depth > MaximumDepth || remainingItems-- <= 0 || builder.Length >= MaximumCharacters) { truncated = true; return; }
            switch (current?.ValueCase)
            {
                case null or AnyValue.ValueOneofCase.None: Append("null"); break;
                case AnyValue.ValueOneofCase.StringValue:
                    if (depth == 0) Append(current.StringValue); else AppendQuoted(current.StringValue);
                    break;
                case AnyValue.ValueOneofCase.BoolValue: Append(current.BoolValue ? "true" : "false"); break;
                case AnyValue.ValueOneofCase.IntValue: Append(current.IntValue.ToString(CultureInfo.InvariantCulture)); break;
                case AnyValue.ValueOneofCase.DoubleValue: Append(current.DoubleValue.ToString("R", CultureInfo.InvariantCulture)); break;
                case AnyValue.ValueOneofCase.BytesValue:
                    var bytes = current.BytesValue.Span;
                    var length = Math.Min(bytes.Length, 1024);
                    Append("base64:"); Append(Convert.ToBase64String(bytes[..length]));
                    if (length < bytes.Length) truncated = true;
                    break;
                case AnyValue.ValueOneofCase.ArrayValue:
                    Append("[");
                    for (var i = 0; i < current.ArrayValue.Values.Count; i++)
                    {
                        if (remainingItems <= 0 || builder.Length >= MaximumCharacters) { truncated = true; break; }
                        if (i > 0) Append(", ");
                        Write(current.ArrayValue.Values[i], depth + 1);
                    }
                    Append("]"); break;
                case AnyValue.ValueOneofCase.KvlistValue:
                    Append("{");
                    for (var i = 0; i < current.KvlistValue.Values.Count; i++)
                    {
                        if (remainingItems <= 0 || builder.Length >= MaximumCharacters) { truncated = true; break; }
                        if (i > 0) Append(", ");
                        var pair = current.KvlistValue.Values[i];
                        AppendQuoted(pair.Key); Append(": "); Write(pair.Value, depth + 1);
                    }
                    Append("}"); break;
                default: Append("[無法解析的值]"); break;
            }
        }
        Write(value, 0);
        return (builder.ToString(), truncated);
    }
}
