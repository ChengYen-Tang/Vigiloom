namespace Vigiloom.Agent.Core.Logs;

/// <summary>Bounded progress through an oversized record without consuming the following record.</summary>
public readonly record struct TextDiscardResult(int ConsumedBytes, bool AtLineStart, bool Complete);

public static class TextDiscardScanner
{
    public static TextDiscardResult Scan(ReadOnlySpan<byte> input, TextRecordMode mode, bool atLineStart)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var offset = 0;
        while (offset < input.Length)
        {
            if (mode == TextRecordMode.IndentedContinuation && atLineStart)
            {
                if (input[offset] is not ((byte)' ' or (byte)'\t')) return new(offset, true, true);
                atLineStart = false;
            }
            var newline = input[offset..].IndexOf((byte)'\n');
            if (newline < 0) return new(input.Length, false, false);
            offset += newline + 1;
            if (mode == TextRecordMode.SingleLine) return new(offset, true, true);
            atLineStart = true;
        }
        return new(offset, atLineStart, false);
    }
}
