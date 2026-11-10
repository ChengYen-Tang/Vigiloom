using System.Text;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Logs;

public enum TextRecordMode { SingleLine, IndentedContinuation }
public enum TextRecordStatus { Record, NeedMore, InvalidEncoding, Oversized }
/// <param name="GapCandidateBytes">Known invalid-record boundary; may be consumed only with a durable gap.</param>
public readonly record struct TextRecordResult(TextRecordStatus Status, int ConsumedBytes = 0, string? Content = null, int? GapCandidateBytes = null);

/// <summary>Finds one UTF-8 record without advancing the underlying durable source cursor.</summary>
public static class TextRecordParser
{
    public const int MaximumRecordBytes = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <param name="flush">True only when the caller's configured idle/end-of-source policy closes the pending record.</param>
    public static TextRecordResult Parse(ReadOnlySpan<byte> input, TextRecordMode mode, bool flush)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (input.IsEmpty) return new(TextRecordStatus.NeedMore);
        var end = 0;
        var firstLine = true;
        while (end < input.Length)
        {
            if (!firstLine && mode == TextRecordMode.IndentedContinuation && input[end] is not ((byte)' ' or (byte)'\t'))
                return Decode(input[..end]);
            var remaining = input[end..];
            var newline = remaining.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (input.Length > MaximumRecordBytes) return new(TextRecordStatus.Oversized);
                return flush ? Decode(input) : new(TextRecordStatus.NeedMore);
            }
            end += newline + 1;
            if (end > MaximumRecordBytes)
                return new(TextRecordStatus.Oversized, GapCandidateBytes: mode == TextRecordMode.SingleLine ? end : null);
            if (mode == TextRecordMode.SingleLine) return Decode(input[..end]);
            firstLine = false;
        }
        return flush ? Decode(input[..end]) : new(TextRecordStatus.NeedMore);
    }

    private static TextRecordResult Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var content = StrictUtf8.GetString(bytes);
            return content.Length <= AgentLogBatchCodec.MaximumContentCharacters
                ? new(TextRecordStatus.Record, bytes.Length, content) : new(TextRecordStatus.Oversized, GapCandidateBytes: bytes.Length);
        }
        catch (DecoderFallbackException) { return new(TextRecordStatus.InvalidEncoding, GapCandidateBytes: bytes.Length); }
    }
}
