using System.Text.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Logs;

/// <summary>Reads UTF-8 text records and only proposes positions covering the exact returned record or reported gap.</summary>
public sealed class TextFileLogReader(string path, AgentTextLogMode mode, TimeProvider clock) : ILogSourceReader
{
    private const int ReadBytes = TextRecordParser.MaximumRecordBytes + 1;
    private static readonly TimeSpan IdleFlush = TimeSpan.FromSeconds(5);

    public async ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token)
    {
        var selectedMode = mode switch
        {
            AgentTextLogMode.SingleLine => TextRecordMode.SingleLine,
            AgentTextLogMode.IndentedContinuation => TextRecordMode.IndentedContinuation,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var state = Decode(cursor);
        var read = await TextFileSource.ReadAsync(path, state?.File, ReadBytes, token);
        if (read.Status == TextFileReadStatus.CursorInvalidated) return Lost();
        if (read.Status == TextFileReadStatus.End)
        {
            if (state is { Discarding: true, File: not null } && IsIdle())
                return Gap(Encode(new(state.File, selectedMode, false, false)), AgentLogGapReason.Oversized, null);
            return new(LogReadStatus.End);
        }
        if (read.NextCursor is null) throw new InvalidDataException("A text chunk must carry its candidate cursor.");

        if (state?.Discarding == true)
            return await DiscardAsync(state, read, state.Mode, selectedMode, token);
        var parsed = TextRecordParser.Parse(read.Data.Span, selectedMode, read.Data.Length < ReadBytes && IsIdle());
        if (parsed.Status == TextRecordStatus.NeedMore) return new(LogReadStatus.End);
        if (parsed.Status == TextRecordStatus.Oversized && parsed.GapCandidateBytes is null)
            return await DiscardAsync(state, read, selectedMode, selectedMode, token);
        var consumed = parsed.Status == TextRecordStatus.Record ? parsed.ConsumedBytes : parsed.GapCandidateBytes ?? 0;
        if (consumed <= 0) throw new InvalidDataException("Text parser returned no safe boundary.");
        var exact = await ExactAsync(state?.File, read, consumed, token);
        if (exact is null) return Lost();
        var next = Encode(new(exact, selectedMode, false, false));
        return parsed.Status switch
        {
            TextRecordStatus.Record => new(LogReadStatus.Record, next, new(null, parsed.Content!)),
            TextRecordStatus.InvalidEncoding => Gap(next, AgentLogGapReason.InvalidEncoding, 1),
            TextRecordStatus.Oversized => Gap(next, AgentLogGapReason.Oversized, 1),
            _ => throw new InvalidDataException("Unexpected text parser status.")
        };
    }

    private async Task<LogReadResult> DiscardAsync(Position? state, TextFileReadResult chunk, TextRecordMode discardMode,
        TextRecordMode selectedMode, CancellationToken token)
    {
        var discarded = TextDiscardScanner.Scan(chunk.Data.Span, discardMode, state?.AtLineStart ?? false);
        TextFileCursor? exact;
        if (discarded.ConsumedBytes == 0) exact = state?.File;
        else exact = await ExactAsync(state?.File, chunk, discarded.ConsumedBytes, token);
        if (exact is null) return Lost();
        var complete = discarded.Complete || discarded.ConsumedBytes == chunk.Data.Length && chunk.Data.Length < ReadBytes && IsIdle();
        var next = Encode(new(exact, complete ? selectedMode : discardMode, !complete,
            !complete && discarded.AtLineStart));
        return Gap(next, AgentLogGapReason.Oversized, null);
    }

    private async Task<TextFileCursor?> ExactAsync(TextFileCursor? current, TextFileReadResult chunk, int count, CancellationToken token)
    {
        if (count == chunk.Data.Length) return chunk.NextCursor;
        var exact = await TextFileSource.ReadAsync(path, current, count, token);
        return exact.Status == TextFileReadStatus.Data && exact.NextCursor is not null && chunk.NextCursor is not null &&
            exact.NextCursor.Identity == chunk.NextCursor.Identity && exact.Data.Span.SequenceEqual(chunk.Data.Span[..count])
            ? exact.NextCursor : null;
    }

    private bool IsIdle()
    {
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        var age = clock.GetUtcNow() - modified;
        return age >= IdleFlush;
    }

    private static LogReadResult Lost() => Gap(NativeLogCursor.Restart, AgentLogGapReason.CursorLost, null);
    private static LogReadResult Gap(string cursor, AgentLogGapReason reason, long? count) => new(LogReadStatus.Gap, cursor, Gap: new(reason, count));
    private static string Encode(Position position) => NativeLogCursor.Encode(JsonSerializer.Serialize(position));

    private static Position? Decode(string? cursor)
    {
        var value = NativeLogCursor.Decode(cursor);
        if (value is null) return null;
        try
        {
            var state = JsonSerializer.Deserialize<Position>(value);
            if (state?.File is null || !Enum.IsDefined(state.Mode) || (!state.Discarding && state.AtLineStart))
                throw new InvalidDataException("Invalid text source cursor.");
            return state;
        }
        catch (JsonException exception) { throw new InvalidDataException("Invalid text source cursor.", exception); }
    }

    private sealed record Position(TextFileCursor File, TextRecordMode Mode, bool Discarding, bool AtLineStart);
}
